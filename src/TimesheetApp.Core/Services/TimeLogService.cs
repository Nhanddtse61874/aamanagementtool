using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Business rules for time logging: precision/weekday/8h validation, day-total reads,
/// rounding, and atomic smart-input apply (with pre-bulk backup). No System.Windows.* / no Dapper.
/// (XC-02/03/04/05, XC-10, RPT-04)</summary>
public sealed class TimeLogService : ITimeLogService
{
    private const decimal DayCap = 8m;

    private readonly ITimeLogRepository _logs;
    private readonly IUserRepository _users;
    private readonly ITaskRepository _tasks;
    private readonly IBacklogRepository _requests;
    private readonly ITeamRepository _teams;
    private readonly ICurrentTeamService _currentTeam;
    private readonly IDbBackupHelper _backup;
    private readonly IClock _clock;
    private readonly IAppConfig _config;
    private readonly IJournalWarningSink _journalWarnings;
    private readonly IHolidayRepository _holidays;   // HOL-02: a marked holiday is a non-working day
    private readonly IWorkingDayCalculator _calc;    // M8.2: the one shared "is this a working day?" rule

    public TimeLogService(
        ITimeLogRepository logs, IUserRepository users, ITaskRepository tasks,
        IBacklogRepository requests, ITeamRepository teams, ICurrentTeamService currentTeam,
        IDbBackupHelper backup, IClock clock,
        IAppConfig config, IJournalWarningSink journalWarnings, IHolidayRepository holidays,
        // Optional + trailing so DI injects the singleton while the existing tests keep their 11-arg call.
        IWorkingDayCalculator? calc = null)
    {
        _logs = logs; _users = users; _tasks = tasks; _requests = requests; _teams = teams;
        _currentTeam = currentTeam; _backup = backup; _clock = clock;
        _config = config; _journalWarnings = journalWarnings; _holidays = holidays;
        _calc = calc ?? new WorkingDayCalculator();
    }

    public async Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours)
    {
        if (hours <= 0m) return Err("Hours must be greater than 0.");
        if (HasMoreThanOneDecimal(hours)) return Err("Hours may have at most 1 decimal place.");
        if (IsWeekend(date)) return Err("Time can only be logged Monday–Friday.");
        if (await _holidays.IsHolidayAsync(date)) return Err($"{date:yyyy-MM-dd} is a holiday."); // HOL-02

        var rounded = Round1(hours);
        if (rounded > DayCap) return Err($"A single cell cannot exceed {DayCap}h."); // XC-02

        // XC-03: read the day's OTHER logs from storage and check the whole-day total after merge.
        var sameDay = await _logs.GetByUserAndRangeAsync(userId, date, date);
        var otherTasksTotal = sameDay.Where(l => l.TaskId != taskId).Sum(l => l.Hours);
        if (otherTasksTotal + rounded > DayCap)
            return Err($"Total for {date:yyyy-MM-dd} would be {otherTasksTotal + rounded}h (> {DayCap}h).");

        await _logs.UpsertAsync(new TimeLog(0, userId, taskId, date, rounded, _clock.UtcNow));
        return Ok();
    }

    public Task ClearCellAsync(int userId, int taskId, DateOnly date)
        => _logs.DeleteAsync(userId, taskId, date); // TS-03 empty=0 => delete

    // --- M8.3: the version-checked siblings (the web API's write path) ---------------------------
    //
    // Validation runs FIRST, then the checked write. That ordering is not incidental: it is what lets a
    // broken rule be a RESULT (Ok=false, nothing written -> HTTP 400) while a stale version is an
    // EXCEPTION (-> HTTP 409). The rules must still bite here, or the API becomes the one caller that
    // can log 40 hours on a Sunday holiday.
    //
    // Swapping UpsertAsync -> UpsertCheckedAsync is the whole behavioural change, and it is safe for a
    // reason worth stating: the 8h-cap read filters `l.TaskId != taskId`, so it only ever reads OTHER
    // tasks' rows, while the write touches only THIS task's cell. The set validation reads and the set
    // the write mutates are disjoint — validation cannot be invalidated by the write it guards.

    public async Task<SaveCellResult> SaveCellCheckedAsync(
        int userId, int taskId, DateOnly date, decimal hours, long? expectedVersion)
    {
        var (error, rounded) = await ValidateCellAsync(userId, taskId, date, hours);
        if (error is not null) return new SaveCellResult(false, error, 0);   // rule broken => NOTHING written

        // expectedVersion is passed through UNNORMALIZED: null is a real assertion ("I believe this cell
        // is empty"), not a missing value, and UpsertCheckedAsync distinguishes it from a version.
        var newVersion = await _logs.UpsertCheckedAsync(
            new TimeLog(0, userId, taskId, date, rounded, _clock.UtcNow), expectedVersion);
        return new SaveCellResult(true, null, newVersion);
    }

    // Mirrors ClearCellAsync (which validates nothing) — only the repository write differs.
    public Task ClearCellCheckedAsync(int userId, int taskId, DateOnly date, long expectedVersion)
        => _logs.DeleteCheckedAsync(userId, taskId, date, expectedVersion);

    // The single-cell rule set, in SaveCellAsync's exact order, returning the first broken rule's message
    // or the value to write. SaveCellAsync is left byte-for-byte as it is (WPF calls it and WPF must keep
    // working until M8.10 deletes it), so these rules currently exist in two places. TimeLogServiceCheckedTests
    // .Checked_and_unchecked_saves_reject_identical_inputs pins them together: any drift fails the suite
    // loudly rather than silently exempting the web from a rule. Collapse the two when the unchecked
    // overload dies with WPF.
    private async Task<(string? Error, decimal Rounded)> ValidateCellAsync(
        int userId, int taskId, DateOnly date, decimal hours)
    {
        if (hours <= 0m) return ("Hours must be greater than 0.", 0m);
        if (HasMoreThanOneDecimal(hours)) return ("Hours may have at most 1 decimal place.", 0m);
        if (IsWeekend(date)) return ("Time can only be logged Monday–Friday.", 0m);
        if (await _holidays.IsHolidayAsync(date)) return ($"{date:yyyy-MM-dd} is a holiday.", 0m); // HOL-02

        var rounded = Round1(hours);
        if (rounded > DayCap) return ($"A single cell cannot exceed {DayCap}h.", 0m); // XC-02

        // XC-03: the day's OTHER logs (this task's own row is the one being replaced, so it is excluded).
        var sameDay = await _logs.GetByUserAndRangeAsync(userId, date, date);
        var otherTasksTotal = sameDay.Where(l => l.TaskId != taskId).Sum(l => l.Hours);
        if (otherTasksTotal + rounded > DayCap)
            return ($"Total for {date:yyyy-MM-dd} would be {otherTasksTotal + rounded}h (> {DayCap}h).", 0m);

        return (null, rounded);
    }

    public async Task<SaveResult> ValidateDayTotalsAsync(int userId, IReadOnlyList<CellAssignment> cells, int taskId)
    {
        foreach (var dayGroup in cells.GroupBy(c => c.Date))
        {
            if (IsWeekend(dayGroup.Key)) return Err($"{dayGroup.Key:yyyy-MM-dd} is a weekend.");
            if (await _holidays.IsHolidayAsync(dayGroup.Key))      // HOL-02: match the single-cell guard
                return Err($"{dayGroup.Key:yyyy-MM-dd} is a holiday.");

            var sameDay = await _logs.GetByUserAndRangeAsync(userId, dayGroup.Key, dayGroup.Key);
            var otherTasksTotal = sameDay.Where(l => l.TaskId != taskId).Sum(l => l.Hours);
            var proposed = dayGroup.Sum(c => Round1(c.Hours));
            if (otherTasksTotal + proposed > DayCap)
                return Err($"{dayGroup.Key:yyyy-MM-dd}: {otherTasksTotal + proposed}h exceeds {DayCap}h.");
        }
        return Ok();
    }

    public async Task<SaveResult> ApplySmartInputAsync(int userId, int taskId, IReadOnlyList<CellAssignment> cells)
    {
        var validation = await ValidateDayTotalsAsync(userId, cells, taskId);
        if (!validation.Ok) return validation;

        await _backup.BackupAsync(); // XC-10: backup BEFORE the bulk write

        var logs = cells
            .Select(c => new TimeLog(0, userId, taskId, c.Date, Round1(c.Hours), _clock.UtcNow))
            .ToList();
        await _logs.UpsertBatchAsync(logs); // SI-05 atomic batch

        // XC-09: after the bulk commit the rollback journal must be gone. A lingering
        // "<db>-journal" means the batch was interrupted; surface it instead of swallowing.
        if (!SqliteMaintenance.IsJournalGone(_config.DbPath))
            _journalWarnings.Warn(
                $"A SQLite rollback journal persists next to '{_config.DbPath}' after a smart-input apply. " +
                "The last bulk write may have been interrupted; verify the database integrity.");

        return Ok();
    }

    // --- Multi-task smart-fill (SI redesign) -----------------------------------------------------

    public async Task<SaveResult> ValidateSmartFillAsync(int userId, IReadOnlyList<SmartFillTask> tasks)
    {
        var flat = tasks
            .SelectMany(t => t.Cells.Where(c => c.Hours > 0m).Select(c => (t.TaskId, c.Date, Hours: Round1(c.Hours))))
            .ToList();
        if (flat.Count == 0) return Err("Select at least one task and enter hours to fill.");

        foreach (var date in flat.Select(x => x.Date).Distinct())
        {
            if (IsWeekend(date)) return Err($"{date:yyyy-MM-dd} is a weekend.");
            if (await _holidays.IsHolidayAsync(date))              // HOL-02: match the single-cell guard
                return Err($"{date:yyyy-MM-dd} is a holiday.");
        }

        var checkedIds = tasks.Select(t => t.TaskId).ToHashSet();
        var from = flat.Min(x => x.Date);
        var to = flat.Max(x => x.Date);
        var stored = await _logs.GetByUserAndRangeAsync(userId, from, to);

        // For each day: hours already stored on OTHER tasks (the checked tasks get overwritten) plus
        // the sum of all checked tasks' proposed hours must stay within the 8h cap (XC-03).
        foreach (var dayGroup in flat.GroupBy(x => x.Date))
        {
            var otherStored = stored
                .Where(l => l.WorkDate == dayGroup.Key && !checkedIds.Contains(l.TaskId))
                .Sum(l => l.Hours);
            var proposed = dayGroup.Sum(x => x.Hours);
            if (otherStored + proposed > DayCap)
                return Err($"{dayGroup.Key:yyyy-MM-dd}: {otherStored + proposed}h exceeds {DayCap}h.");
        }
        return Ok();
    }

    public async Task<SaveResult> ApplySmartFillAsync(int userId, IReadOnlyList<SmartFillTask> tasks)
    {
        var validation = await ValidateSmartFillAsync(userId, tasks);
        if (!validation.Ok) return validation;

        await _backup.BackupAsync(); // XC-10: backup BEFORE the bulk write

        var logs = tasks
            .SelectMany(t => t.Cells.Where(c => c.Hours > 0m)
                .Select(c => new TimeLog(0, userId, t.TaskId, c.Date, Round1(c.Hours), _clock.UtcNow)))
            .ToList();
        await _logs.UpsertBatchAsync(logs);

        if (!SqliteMaintenance.IsJournalGone(_config.DbPath))
            _journalWarnings.Warn(
                $"A SQLite rollback journal persists next to '{_config.DbPath}' after a smart-fill apply. " +
                "The last bulk write may have been interrupted; verify the database integrity.");

        return Ok();
    }

    public async Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek)
    {
        var monday = DateHelpers.MondayOf(mondayOfWeek);
        var friday = monday.AddDays(4);
        var logs = await _logs.GetByUserAndRangeAsync(userId, monday, friday);
        // P10 (TM-06): the Log Work grid shows ONLY the active team's tasks (incl. its DEFAULT).
        var tasks = await _tasks.GetActiveForTimesheetAsync(_currentTeam.ActiveTeamId);

        var byKey = logs.ToLookup(l => l.TaskId);
        var rows = tasks.Select(t =>
        {
            decimal? On(DateOnly d) => byKey[t.Id].Where(l => l.WorkDate == d).Select(l => (decimal?)l.Hours).FirstOrDefault();
            // [ASSUMED] BacklogCode left empty here. GetWeekAsync backs TS-01/02/05 (P3 scope);
            // ITaskRepository.GetActiveForTimesheetAsync (spec §3) returns TaskItem which has no
            // backlog_code. P3 either (a) extends that query to project backlog_code, or (b) the
            // VM joins it from IBacklogRepository when shaping rows. P2 ships the structurally
            // correct WeekGrid with hours populated; BacklogCode population is a P3 concern.
            return new WeekRow(t.Id, BacklogCode: "", t.TaskName, t.OrderIndex,
                On(monday), On(monday.AddDays(1)), On(monday.AddDays(2)), On(monday.AddDays(3)), On(monday.AddDays(4)));
        }).ToList();

        return new WeekGrid(monday, rows);
    }

    public async Task<IReadOnlyList<WeekBacklogGroup>> GetWeekGroupedAsync(int userId, DateOnly mondayOfWeek)
    {
        var monday = DateHelpers.MondayOf(mondayOfWeek);
        var friday = monday.AddDays(4);

        var logs = await _logs.GetByUserAndRangeAsync(userId, monday, friday);
        var byTask = logs.ToLookup(l => l.TaskId);
        return await BuildGroupsAsync(monday, (taskId, d) =>
            byTask[taskId].Where(l => l.WorkDate == d).Select(l => (decimal?)l.Hours).FirstOrDefault());
    }

    public async Task<IReadOnlyList<WeekBacklogGroup>> GetWeekGroupedAllUsersAsync(DateOnly mondayOfWeek)
    {
        var monday = DateHelpers.MondayOf(mondayOfWeek);
        var friday = monday.AddDays(4);

        // Team view: sum hours across ALL users per (task, day). Export rows already join every user.
        // P10 (TM-06): scope to the active team so the team grid never aggregates another team's hours.
        var rows = await _logs.GetExportRowsAsync(monday, friday, null, ActiveTeamScope());
        var summed = rows
            .GroupBy(r => (r.TaskId, r.WorkDate))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Hours));
        return await BuildGroupsAsync(monday, (taskId, d) =>
            summed.TryGetValue((taskId, d), out var h) ? h : (decimal?)null);
    }

    // Shared group shaping: one group per backlog (incl. DEFAULT + empty), tasks ordered, with the
    // per-(task,day) hours supplied by the caller (single user vs team aggregate).
    private async Task<IReadOnlyList<WeekBacklogGroup>> BuildGroupsAsync(
        DateOnly monday, Func<int, DateOnly, decimal?> hoursFor)
    {
        // P10 (TM-06): active team's backlogs only (incl. its DEFAULT + empty ones), and its tasks.
        var requests = await _requests.SearchAsync(null, ActiveTeamScope());
        var tasks = await _tasks.GetActiveForTimesheetAsync(_currentTeam.ActiveTeamId);
        var tasksByBacklog = tasks.ToLookup(t => t.BacklogId);

        // v4: resolve assignee ids -> names (GetAll so a deactivated assignee still renders).
        var allUsers = await _users.GetAllAsync() ?? Array.Empty<User>();
        var userNames = allUsers.ToDictionary(u => u.Id, u => u.Name);

        // Order by backlog_code so DEFAULT-vs-others is stable across reloads.
        return requests
            .OrderBy(r => r.BacklogCode, StringComparer.Ordinal)
            .Select(r =>
            {
                var rows = tasksByBacklog[r.Id]
                    .OrderBy(t => t.OrderIndex)
                    .Select(t => new WeekRow(t.Id, r.BacklogCode, t.TaskName, t.OrderIndex,
                        hoursFor(t.Id, monday), hoursFor(t.Id, monday.AddDays(1)),
                        hoursFor(t.Id, monday.AddDays(2)), hoursFor(t.Id, monday.AddDays(3)),
                        hoursFor(t.Id, monday.AddDays(4))))
                    .ToList();
                var assignee = r.AssigneeUserId is { } uid && userNames.TryGetValue(uid, out var n) ? n : null;
                return new WeekBacklogGroup(r.Id, r.BacklogCode, r.Project, rows, r.PeriodMonth, r.Type, assignee);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<User>> GetUsersMissingLogsAsync(int workdayWindowN)
    {
        // P10 (RPT-04): the "chưa log" banner is scoped to the ACTIVE TEAM's members. teamId 0 (no
        // team resolved yet) => empty (no rows), never all-teams (R6).
        var activeTeamId = _currentTeam.ActiveTeamId;
        if (activeTeamId == 0) return Array.Empty<User>();

        var window = await LastNWorkingDaysAsync(_clock.Today, workdayWindowN); // includes today (RPT-04)
        var earliest = window.Min();
        var withLogs = (await _logs.GetUserIdsWithLogsInRangeAsync(earliest, _clock.Today)).ToHashSet();

        // GetUserIdsWithLogsInRangeAsync has no team filter (W1) -> scope by team membership instead.
        var memberIds = (await _teams.GetUserIdsForTeamAsync(activeTeamId)).ToHashSet();
        var active = await _users.GetActiveAsync();
        return active
            .Where(u => memberIds.Contains(u.Id) && !withLogs.Contains(u.Id))
            .ToList();
    }

    // Active-team scope for the team filter on read APIs: the active team id, or an EMPTY list when
    // no team is resolved yet (teamId 0 == empty, R6) so a pre-setup DB never leaks all teams.
    private IReadOnlyList<int> ActiveTeamScope()
    {
        var id = _currentTeam.ActiveTeamId;
        return id == 0 ? Array.Empty<int>() : new[] { id };
    }

    // ---- helpers ----
    private static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    private static decimal Round1(decimal v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    private static bool HasMoreThanOneDecimal(decimal v) => v != Round1(v);

    // M8.2: walk back to the last N WORKING days. This used to exclude weekends only, which contradicted
    // WorkingDayCalculator (weekends AND holidays) — so the "hasn't logged in N days" banner counted
    // public holidays against people. It now delegates to the one shared definition of a working day.
    private async Task<List<DateOnly>> LastNWorkingDaysAsync(DateOnly today, int n)
    {
        var holidays = (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();
        var days = new List<DateOnly>();
        var d = today;
        while (days.Count < n)
        {
            if (_calc.IsWorkingDay(d, holidays)) days.Add(d);
            d = d.AddDays(-1);
        }
        return days;
    }

    private static SaveResult Ok() => new(true, null);
    private static SaveResult Err(string message) => new(false, message);
}
