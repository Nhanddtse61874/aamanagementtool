using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Outcome of a version-checked cell save (M8.3).
///
/// The two failure modes are deliberately NOT the same shape, because they are not the same kind of
/// event and the web must answer them differently:
/// <list type="bullet">
/// <item>A broken business rule (8h cap, holiday, weekend, precision) is a RESULT — <c>Ok == false</c>
/// with the message — and becomes an HTTP 400. It is the caller's input that is wrong.</item>
/// <item>A version conflict is an EXCEPTION (<c>ConcurrencyConflictException</c>) and becomes an HTTP
/// 409. The caller's input was fine; the world moved underneath it.</item>
/// </list>
/// Folding a conflict into <c>Ok == false</c> would make the two indistinguishable at the transport
/// boundary, and a client cannot retry a 400 but must re-read and retry a 409.</summary>
/// <param name="RowVersion">The row_version AFTER a successful write — the caller's next
/// expectedVersion, so it never has to re-read (a read-back is racy). Meaningless when <c>Ok</c> is
/// false, because nothing was written.</param>
public readonly record struct SaveCellResult(bool Ok, string? Error, long RowVersion);

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

    // --- M8.3: the version-aware siblings. THE WEB API MUST USE THESE. ---
    //
    // M8.2 built optimistic concurrency across eight repositories and stopped one layer short: no service
    // method ever passed an expectedVersion, so a caller routed through this service could not conflict,
    // and a caller that went around it to reach ITimeLogRepository directly would bypass every business
    // rule below. These two methods are what let the API have both.
    //
    // Same validation, same order, in the same place — the ONLY difference from the pair above is which
    // repository write they end on (UpsertCheckedAsync / DeleteCheckedAsync instead of the bump-only
    // UpsertAsync / DeleteAsync, whose own docs say a version-aware caller must not use them).

    /// <summary>Version-checked single-cell save. Runs the full rule set FIRST (>0, ≤1 decimal,
    /// weekday-only, holiday, per-cell and whole-day 8h cap) and only then performs the checked write —
    /// so a rule violation is reported as <c>Ok == false</c> and NOTHING is written, while a stale
    /// version throws.</summary>
    /// <param name="expectedVersion">The row_version the caller believes the cell is at.
    /// <c>null</c> is MEANINGFUL and must not be normalized away: it asserts "I believe this cell is
    /// empty". An upsert therefore has five outcomes, not four — see
    /// <see cref="Data.Repositories.ITimeLogRepository.UpsertCheckedAsync"/>.</param>
    /// <exception cref="Data.ConcurrencyConflictException">The cell moved on, or was deleted, or was
    /// created by someone else while the caller believed it empty.</exception>
    Task<SaveCellResult> SaveCellCheckedAsync(int userId, int taskId, DateOnly date, decimal hours, long? expectedVersion);

    /// <summary>Version-checked clear (TS-03 empty cell => remove row). Clearing a cell that is already
    /// gone is itself a conflict — the caller is acting on a version that no longer exists.</summary>
    /// <exception cref="Data.ConcurrencyConflictException">Version moved on, or the row is already gone.</exception>
    Task ClearCellCheckedAsync(int userId, int taskId, DateOnly date, long expectedVersion);
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
