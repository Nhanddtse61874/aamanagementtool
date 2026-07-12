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

    // Check-and-bump (v10/M8.2): issues are collaborative (DR-04, no owner gate), so this throws
    // ConcurrencyConflictException when issue.RowVersion no longer matches the row (changed or deleted).
    Task UpdateIssueAsync(StandupIssue issue);
    Task DeleteIssueAsync(int issueId);
}
