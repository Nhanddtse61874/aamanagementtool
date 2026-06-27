using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract for Backlog items (formerly Request).
public interface IBacklogRepository
{
    // null term => all (matches code OR project). teamIds: null => all teams (preserves existing
    // behavior); a list filters to those teams; an EMPTY list => no teams (teamId 0 is "empty", R6).
    Task<IReadOnlyList<Backlog>> SearchAsync(string? term, IReadOnlyList<int>? teamIds = null);
    Task<Backlog?> GetByIdAsync(int id);
    Task<Backlog?> GetByCodeAsync(string backlogCode);             // find hidden 'DEFAULT' (DATA-03)
    Task<Backlog?> GetDefaultForTeamAsync(int teamId);             // P10: DEFAULT is unique per team (TM-04)
    Task<int> InsertAsync(Backlog backlog);
    // Edit name/project + v2 start/end/month/type. changedBy is recorded in BacklogAudit when the
    // audited fields change.
    Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null);
    Task<IReadOnlyList<BacklogAuditEntry>> GetAuditAsync(int backlogId);   // v2 change history
    // No SetActiveAsync — Backlogs are NOT soft-deletable (decision 4).

    // v7 tag links (TAG-02). SetTagsAsync replaces the whole set for one backlog in a single tx.
    Task<IReadOnlyList<int>> GetTagIdsAsync(int backlogId);
    Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForAllAsync();  // bulk, avoids N+1
}
