using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Daily Report data access (DR-02..04, DR-09). SQL + Dapper only; one short connection per method.
public interface IStandupRepository
{
    // All of a user's entries on a day (both sections), ordered by section then order_index (DR-07).
    // P10 (TM-06): teamId null => all teams (preserves existing tests); set => active-team only.
    Task<IReadOnlyList<StandupEntry>> GetEntriesAsync(int userId, DateOnly workDate, int? teamId = null);

    // Every entry on a day across the checked teams (board view, DR-08 / TM-07).
    // teamIds null => all teams (preserves existing tests); a list filters; empty => none (R6).
    Task<IReadOnlyList<StandupEntry>> GetEntriesForDayAsync(DateOnly workDate, IReadOnlyList<int>? teamIds = null);

    // Entries across a date range, ordered by date (weekly archive, DR-09 / TM-08).
    Task<IReadOnlyList<StandupEntry>> GetEntriesForRangeAsync(DateOnly from, DateOnly to, IReadOnlyList<int>? teamIds = null);

    // One entry by id (used to gate edits by owner + work_date), or null.
    Task<StandupEntry?> GetEntryAsync(int entryId);

    Task<int> InsertEntryAsync(StandupEntry entry);   // returns new id
    Task UpdateEntryAsync(StandupEntry entry);
    Task DeleteEntryAsync(int entryId);               // cascades StandupIssues

    // Issues for a set of entries (board/input both load issues in one round-trip).
    Task<IReadOnlyList<StandupIssue>> GetIssuesForEntriesAsync(IReadOnlyList<int> entryIds);
    Task<int> InsertIssueAsync(StandupIssue issue);   // returns new id

    // v10/M8.2. Issues are collaborative (DR-04, no owner gate), so they are the one standup table two
    // people can race. Same PAIR as everywhere else (see IBacklogRepository): UpdateIssueAsync is
    // BUMP-ONLY; UpdateIssueCheckedAsync is CHECK-AND-BUMP and returns the new row_version.
    //
    // expectedVersion is an EXPLICIT argument, deliberately not read off issue.RowVersion. A caller
    // that constructs a StandupIssue from edited fields rather than from a read carries the record's
    // default (0), and a write that trusted the record would reject it — so the version has to travel
    // separately from the data it guards.
    Task UpdateIssueAsync(StandupIssue issue);
    /// <returns>The row_version AFTER the write — the caller's next expectedVersion.</returns>
    /// <exception cref="ConcurrencyConflictException">Version moved on, or the issue is gone.</exception>
    Task<long> UpdateIssueCheckedAsync(StandupIssue issue, long expectedVersion);
    Task DeleteIssueAsync(int issueId);
}
