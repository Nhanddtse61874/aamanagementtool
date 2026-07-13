using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (TimeLogRepository)
// is owned by P2 Task 4 (Wave 2). Report/export joins use INNER JOIN by id with NO is_active
// filter (XC-06) so soft-deleted names still resolve; UpsertBatchAsync is one transaction (SI-05).
public interface ITimeLogRepository
{
    // Upsert on UNIQUE(user_id, task_id, work_date) via INSERT ... ON CONFLICT DO UPDATE (TS-07/SI-05).
    // BUMP-ONLY (M8.2): always increments row_version, never compares it, so it can never conflict.
    // This is the system/legacy write — it carries no version from a client. A version-aware caller
    // (the web API) must use UpsertCheckedAsync instead; this overload silently wins every race.
    Task UpsertAsync(TimeLog log);
    Task DeleteAsync(int userId, int taskId, DateOnly workDate);   // empty cell => remove row (TS-03)

    /// <summary>
    /// Check-and-bump upsert (M8.2 optimistic concurrency). Unlike a plain UPDATE, an upsert has
    /// FIVE outcomes, because the row may be absent on either side of the version check:
    /// <list type="bullet">
    /// <item><c>expectedVersion == null</c> + no row  => INSERT at row_version 1.</item>
    /// <item><c>expectedVersion == null</c> + row exists => conflict (someone else filled this cell).</item>
    /// <item><c>expectedVersion == N</c> + row at N  => UPDATE, row_version becomes N + 1.</item>
    /// <item><c>expectedVersion == N</c> + row at != N => conflict (someone else edited it).</item>
    /// <item><c>expectedVersion == N</c> + NO row    => conflict (someone else DELETED it).</item>
    /// </list>
    /// That last case is the one a naive `ON CONFLICT ... DO UPDATE ... WHERE row_version = @expected`
    /// gets wrong: with no row there is no conflict, so the WHERE never runs and the INSERT resurrects
    /// the deleted row at version 1 while reporting success.
    /// </summary>
    /// <returns>The row_version AFTER the write — the caller's next expectedVersion.</returns>
    /// <exception cref="ConcurrencyConflictException">Any of the three conflict cases above.</exception>
    Task<long> UpsertCheckedAsync(TimeLog log, long? expectedVersion);

    /// <summary>Check-and-delete: clears the cell only if it is still at <paramref name="expectedVersion"/>
    /// (TS-03 empty cell => remove row). Deleting a row that is already gone is itself a conflict —
    /// the caller is acting on a version that no longer exists.</summary>
    /// <exception cref="ConcurrencyConflictException">Version moved on, or the row is already gone.</exception>
    Task DeleteCheckedAsync(int userId, int taskId, DateOnly workDate, long expectedVersion);
    Task<IReadOnlyList<TimeLog>> GetByUserAndRangeAsync(int userId, DateOnly from, DateOnly to);  // week grid + 8h day-total read (XC-03)
    // teamIds: null => all teams (preserves existing tests); a list filters via the Backlogs join;
    // an EMPTY list => no teams (teamId 0 == empty, R6). Projects the owning team (R1 leak fix).
    Task<IReadOnlyList<TimeLogReportRow>> GetReportRowsAsync(int userId, DateOnly from, DateOnly to, IReadOnlyList<int>? teamIds = null); // RPT-01..03
    Task<IReadOnlyList<TimeLogReportRow>> GetExportRowsAsync(DateOnly from, DateOnly to, string? projectFilter, IReadOnlyList<int>? teamIds = null); // EXP-01..04
    Task<IReadOnlyList<int>> GetUserIdsWithLogsInRangeAsync(DateOnly from, DateOnly to);  // RPT-04 single-range scan
    // Batch upsert in one transaction for smart-input apply (SI-05 atomicity).
    // BUMPS but does NOT CHECK (M8.2). Smart Fill is a server-side computation, not an echo of a
    // version the client read, so it has nothing to check against. It must still bump: if it left
    // row_version alone, a client holding the pre-fill version would still match and would silently
    // overwrite Smart Fill's result — the exact lost update this mechanism exists to prevent.
    Task UpsertBatchAsync(IReadOnlyList<TimeLog> logs);
    // P8 (TL-05/A1): all-time SUM(hours) per backlog for the Task List roll-up. NO is_active filter on
    // the TimeLogs->Tasks join (XC-06): hours of soft-deleted tasks still count toward the backlog.
    Task<IReadOnlyDictionary<int, decimal>> GetLoggedHoursByBacklogAsync();
}
