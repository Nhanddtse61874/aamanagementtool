using TimesheetApp.Models;

namespace TimesheetApp.Services;

// M9 (P1c): the Task List screen's read model, computed SERVER-SIDE.
//
// WHY THIS EXISTS AT ALL. A client cannot build this screen from the endpoints that exist. The chips
// (Late / At risk) come from ScheduleState, and ScheduleState needs logged-hours-per-backlog — which
// no API route exposes (GetLoggedHoursByBacklogAsync had ZERO callers anywhere under TimesheetApp.Api).
// IScheduleStateService was DI-registered in the API and called by no endpoint. So the maths was
// present, correct, and unreachable: the only way to draw the screen was for the client to reimplement
// the pace rule, and a client reimplementation would drift from the server's the first time either
// changed — silently, because both would look plausible. This projection is the fix: the rule is
// evaluated once, in the one place that owns it.
public interface ITaskListService
{
    /// <summary>Builds the Task List screen for a month in ONE round-trip: every non-DEFAULT backlog
    /// in scope, its all-time logged hours, tags, active tasks, computed <c>ScheduleState</c>, and the
    /// Gantt geometry — all from a single snapshot, so the grid and the chart cannot disagree.</summary>
    /// <param name="month">1-12, or <c>0</c> for ALL months (matches the desktop month selector's
    /// "All months" sentinel — it lists every backlog regardless of period, including a null one).</param>
    /// <param name="teamIds">The teams to scope to. An EMPTY list means NO teams and yields no rows —
    /// that is IBacklogRepository's documented R6 semantic, not an accident, and the caller must pass
    /// the user's actual team ids rather than an empty list meaning "don't care".</param>
    Task<TaskListScreen> GetRowsAsync(int year, int month, IReadOnlyList<int> teamIds);
}
