using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract for Backlog items (formerly Request).
public interface IBacklogRepository
{
    Task<IReadOnlyList<Backlog>> SearchAsync(string? term);        // null => all; matches code OR project
    Task<Backlog?> GetByIdAsync(int id);
    Task<Backlog?> GetByCodeAsync(string backlogCode);             // find hidden 'DEFAULT' (DATA-03)
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
