using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Services;

// P20 orchestration: clone a backlog forward one month with Type="Continue". Copies backlog tags + the
// not-Done tasks (with type/assignee + task tags); keeps progress; leaves the source untouched. Blocks a
// duplicate (same code already in targetPeriod for the backlog's team). No SQL here — repos only.
public sealed class BacklogContinuationService : IBacklogContinuationService
{
    private const string ContinueType = "Continue";
    private const string DoneStatus = "Done";

    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ICurrentUserService _currentUser;
    private readonly IClock _clock;

    public BacklogContinuationService(
        IBacklogRepository backlogs, ITaskRepository tasks, ICurrentUserService currentUser, IClock clock)
    {
        _backlogs = backlogs;
        _tasks = tasks;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<int> ContinueAsync(int backlogId, string targetPeriod)
    {
        var src = await _backlogs.GetByIdAsync(backlogId);
        if (src is null) return 0;

        // Duplicate guard, scoped to the backlog's team (null team => all teams). Uses SearchAsync (not
        // GetByCodeAsync, which throws when the code is non-unique across months).
        var teamIds = src.TeamId is { } t ? new[] { t } : (IReadOnlyList<int>?)null;
        var existing = await _backlogs.SearchAsync(null, teamIds);
        if (existing.Any(b => string.Equals(b.BacklogCode, src.BacklogCode, StringComparison.Ordinal)
                              && string.Equals(b.PeriodMonth, targetPeriod, StringComparison.Ordinal)))
            return 0;   // already continued to that month

        var uid = _currentUser.Current?.Id;
        var uname = _currentUser.Current?.Name;

        // Clone the backlog: new row, next period, Type=Continue, keep progress + all other fields.
        var copy = src with { Id = 0, PeriodMonth = targetPeriod, Type = ContinueType, CreatedAt = _clock.UtcNow };
        var newId = await _backlogs.InsertAsync(copy);

        var backlogTags = await _backlogs.GetTagIdsAsync(src.Id);
        if (backlogTags.Count > 0) await _backlogs.SetTagsAsync(newId, backlogTags, uid, uname);

        // Copy the not-Done tasks (with their type/assignee + tags).
        foreach (var task in (await _tasks.GetActiveByBacklogAsync(src.Id))
                     .Where(t => !string.Equals(t.Status, DoneStatus, StringComparison.Ordinal)))
        {
            var ntid = await _tasks.InsertAsync(
                new TaskItem(0, newId, task.TaskName, task.OrderIndex, true, task.Status));
            if (task.Type is not null || task.AssigneeUserId is not null)
                await _tasks.UpdateExtendedAsync(ntid, task.Type, task.AssigneeUserId, uid, uname);
            var taskTags = await _tasks.GetTagIdsAsync(task.Id);
            if (taskTags.Count > 0) await _tasks.SetTaskTagsAsync(ntid, taskTags, uid, uname);
        }

        await _backlogs.WriteContinuedAuditAsync(newId, src.PeriodMonth, uid, uname);
        return newId;
    }
}
