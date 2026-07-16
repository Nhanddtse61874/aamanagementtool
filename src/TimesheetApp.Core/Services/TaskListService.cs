using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// M9 (P1c): server-side replica of TaskListViewModel.LoadAsync's projection (see ITaskListService for
// why the client cannot do this itself).
//
// TWO THINGS THIS FILE IS CAREFUL ABOUT:
//
// 1. NO N+1. Every lookup is batched and loaded exactly once per call — logged hours, tag links, tasks,
//    tags, users, PCA contacts, holidays. The desktop VM issues one GetTagIdsAsync per TASK on top of
//    this; that feeds the WPF inline tag-picker, is not part of TaskListRow, and is deliberately not
//    replicated here. Cost is a fixed handful of queries regardless of how many backlogs are in scope.
//
// 2. ONE SNAPSHOT. All of it is read before any row is projected, so the chips, the hours and the bars
//    all describe the same instant.
//
// Stateless (teamIds is a PARAMETER, not injected ICurrentTeamService) — which is exactly what keeps it
// Singleton-registrable under the API's ValidateScopes. Injecting the scoped team service here would
// make it a captive dependency and fail the container at Build().
public sealed class TaskListService : ITaskListService
{
    private const string DefaultBacklogCode = "DEFAULT";

    // The desktop month selector's "All months" sentinel (TaskListViewModel.AllMonths) — every backlog
    // regardless of period_month, including a null one.
    private const int AllMonths = 0;

    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ITimeLogRepository _timeLogs;
    private readonly ITagRepository _tags;
    private readonly IPcaContactRepository _pcaContacts;
    private readonly IUserRepository _users;
    private readonly IHolidayRepository _holidays;
    private readonly IWorkingDayCalculator _calc;
    private readonly IScheduleStateService _schedule;
    private readonly IClock _clock;

    public TaskListService(
        IBacklogRepository backlogs, ITaskRepository tasks, ITimeLogRepository timeLogs,
        ITagRepository tags, IPcaContactRepository pcaContacts, IUserRepository users,
        IHolidayRepository holidays, IWorkingDayCalculator calc, IScheduleStateService schedule,
        IClock clock)
    {
        _backlogs = backlogs;
        _tasks = tasks;
        _timeLogs = timeLogs;
        _tags = tags;
        _pcaContacts = pcaContacts;
        _users = users;
        _holidays = holidays;
        _calc = calc;
        _schedule = schedule;
        _clock = clock;
    }

    public async Task<TaskListScreen> GetRowsAsync(int year, int month, IReadOnlyList<int> teamIds)
    {
        var allMonths = month == AllMonths;
        var monthKey = allMonths ? null : $"{year:0000}-{month:00}";
        var today = _clock.Today;

        // DEFAULT is the hidden per-team catch-all backlog that absorbs unassigned time. It is not a
        // work item, has no deadline, and must never appear as a row (DATA-03).
        var scoped = (await _backlogs.SearchAsync(null, teamIds))
            .Where(b => !string.Equals(b.BacklogCode, DefaultBacklogCode, StringComparison.Ordinal))
            .Where(b => allMonths || string.Equals(b.PeriodMonth, monthKey, StringComparison.Ordinal))
            .OrderBy(b => b.BacklogCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backlogIds = scoped.Select(b => b.Id).ToList();

        // --- The batched reads. One each, before any projection (see the header note). ----------------
        var loggedByBacklog = await _timeLogs.GetLoggedHoursByBacklogAsync();
        var tagIdsByBacklog = await _backlogs.GetTagIdsForAllAsync();
        var tasksByBacklog = await _tasks.GetActiveByBacklogsAsync(backlogIds);
        var tagsById = (await _tags.GetAllAsync()).ToDictionary(t => t.Id);
        // GetAll (not GetActive) so a deactivated PCT/PCA still resolves on a historical row (XC-06).
        var userNames = (await _users.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);
        var pcaNames = (await _pcaContacts.GetAllAsync()).ToDictionary(p => p.Id, p => p.Name);
        var holidaySet = (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();

        var rows = new List<TaskListRow>(scoped.Count);
        // The Gantt is built from the backlog + the SAME ScheduleState the row's chip shows, captured
        // here — so a bar's colour can never contradict its row's chip.
        var ganttSource = new List<(Backlog Backlog, ScheduleState State)>(scoped.Count);

        foreach (var b in scoped)
        {
            tasksByBacklog.TryGetValue(b.Id, out var found);
            var tasks = found ?? Array.Empty<TaskItem>();

            var logged = loggedByBacklog.TryGetValue(b.Id, out var h) ? h : 0m;   // absent → 0
            var estimate = b.OfficialEstimateHours ?? b.RoughEstimateHours;        // §7 precedence

            // Done = ≥1 active task AND every active task is "Done" (§6.2).
            //
            // ZERO TASKS IS NOT DONE. Inverting this is the quiet catastrophe in this method: `All()` on
            // an empty sequence is TRUE, so a bare `tasks.All(...)` would mark every empty backlog Done,
            // and Done short-circuits ScheduleState to Normal — so the backlogs with no plan yet, which
            // are precisely the ones most likely to be slipping, would be the ones with no Late chip.
            var isDone = tasks.Count > 0 && tasks.All(t => string.Equals(t.Status, "Done", StringComparison.Ordinal));

            var state = _schedule.Evaluate(
                today, b.StartDate, b.DeadlineInternal, estimate, logged, isDone, holidaySet, _calc);

            var tags = tagIdsByBacklog.TryGetValue(b.Id, out var ids)
                ? ids.Where(tagsById.ContainsKey).Select(id => tagsById[id])
                    .OrderBy(t => t.Id).ToList()        // Q4: custom tags ordered by Tag.Id
                : new List<Tag>();

            var pctName = b.AssigneeUserId is { } uid && userNames.TryGetValue(uid, out var un) ? un : null;
            var pcaName = b.PcaContactId is { } pid && pcaNames.TryGetValue(pid, out var pn) ? pn : null;

            rows.Add(new TaskListRow(
                b.Id, b.BacklogCode, b.Project, b.Type, pctName, pcaName,
                b.AssigneeUserId, b.PcaContactId,
                b.DeadlineInternal, b.DeadlineExternal, b.StartDate, b.EndDate,
                b.ProgressPercent, logged, estimate, state, tags, tasks,
                b.TeamId));   // TL-12: already loaded by SearchAsync — no new query

            ganttSource.Add((b, state));
        }

        // Same geometry the desktop draws — the one implementation, moved to Core in P1a.
        var gantt = GanttBuilder.BuildGantt(ganttSource, holidaySet, _calc);

        return new TaskListScreen(rows, gantt);
    }
}
