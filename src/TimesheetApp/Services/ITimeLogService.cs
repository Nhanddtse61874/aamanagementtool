using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Service contract is VERBATIM from architecture spec §4. Implementation (TimeLogService) is
// owned by P2 Task 6 (Wave 3). Owns all time-logging business rules: precision / weekday / 8h
// validation, day-total reads from storage, AwayFromZero rounding, and atomic smart-input apply
// with a pre-bulk backup. No Dapper, no System.Windows.* — depends only on repo interfaces +
// IClock + IDbBackupHelper.
public interface ITimeLogService
{
    // Single-cell inline edit. Enforces >0, ≤1 decimal, weekday-only, the per-cell 8h check AND
    // the per-save whole-day 8h cap (reads the day's other logs from storage). Rounds to 1 decimal
    // (AwayFromZero) before upsert. (XC-02/03/04/05, TS-07)
    Task<SaveResult> SaveCellAsync(int userId, int taskId, DateOnly date, decimal hours);
    Task ClearCellAsync(int userId, int taskId, DateOnly date);                             // TS-03 (empty=0 => delete)
    Task<WeekGrid> GetWeekAsync(int userId, DateOnly mondayOfWeek);                          // TS-01/02/05
    // Same week data grouped by Backlog — one group per backlog (incl. DEFAULT + empty ones), so the
    // Timesheet tab can render collapsible groups and add a task to an empty backlog inline. (TS-01/02/05)
    Task<IReadOnlyList<WeekBacklogGroup>> GetWeekGroupedAsync(int userId, DateOnly mondayOfWeek);
    // Same shape but hours summed across ALL users — the Entry "Cả team" read-only view (v2).
    Task<IReadOnlyList<WeekBacklogGroup>> GetWeekGroupedAllUsersAsync(DateOnly mondayOfWeek);
    // Ok only when every day in the proposed cell set stays ≤ 8h after merge — used by preview (SI-05).
    Task<SaveResult> ValidateDayTotalsAsync(int userId, IReadOnlyList<CellAssignment> cells, int taskId);
    // Commit a validated smart-input set atomically; backs up before the bulk upsert (XC-10, SI-05).
    Task<SaveResult> ApplySmartInputAsync(int userId, int taskId, IReadOnlyList<CellAssignment> cells);
    // Multi-task smart-fill (SI redesign): validate the COMBINED per-day totals (other stored tasks +
    // every checked task's proposed cells) against the 8h cap, then apply all atomically with a backup.
    Task<SaveResult> ValidateSmartFillAsync(int userId, IReadOnlyList<SmartFillTask> tasks);
    Task<SaveResult> ApplySmartFillAsync(int userId, IReadOnlyList<SmartFillTask> tasks);
    // Active users with zero logs in LastNWorkingDays(today, N) — today included (RPT-04).
    Task<IReadOnlyList<User>> GetUsersMissingLogsAsync(int workdayWindowN);
}
