using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Daily Report data access (DR-02..04, DR-09). SQL + Dapper only; one short connection per method.
public interface IStandupRepository
{
    // All of a user's entries on a day (both sections), ordered by section then order_index (DR-07).
    Task<IReadOnlyList<StandupEntry>> GetEntriesAsync(int userId, DateOnly workDate);

    // Every entry on a day across the team (board view, DR-08).
    Task<IReadOnlyList<StandupEntry>> GetEntriesForDayAsync(DateOnly workDate);

    // Entries across a date range, ordered by date (weekly archive, DR-09).
    Task<IReadOnlyList<StandupEntry>> GetEntriesForRangeAsync(DateOnly from, DateOnly to);

    // One entry by id (used to gate edits by owner + work_date), or null.
    Task<StandupEntry?> GetEntryAsync(int entryId);

    Task<int> InsertEntryAsync(StandupEntry entry);   // returns new id
    Task UpdateEntryAsync(StandupEntry entry);
    Task DeleteEntryAsync(int entryId);               // cascades StandupIssues

    // Issues for a set of entries (board/input both load issues in one round-trip).
    Task<IReadOnlyList<StandupIssue>> GetIssuesForEntriesAsync(IReadOnlyList<int> entryIds);
    Task<int> InsertIssueAsync(StandupIssue issue);   // returns new id
    Task UpdateIssueAsync(StandupIssue issue);
    Task DeleteIssueAsync(int issueId);
}
