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
    // audited fields change. v9 (B2): auditNote is stored in BacklogAudit.note for deadline fields only.
    Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null,
        string? auditNote = null);
    Task<IReadOnlyList<BacklogAuditEntry>> GetAuditAsync(int backlogId);   // v2 change history
    // Batch form of GetAuditAsync: one IN-query across many backlogs, grouped by backlog_id (avoids
    // N+1). Each group is ordered id DESC like the single-backlog query; absent ids are missing keys.
    Task<IReadOnlyDictionary<int, IReadOnlyList<BacklogAuditEntry>>> GetAuditForBacklogsAsync(IReadOnlyList<int> backlogIds);
    // No SetActiveAsync — Backlogs are NOT soft-deletable (decision 4).

    // v7 tag links (TAG-02). SetTagsAsync replaces the whole set for one backlog in a single tx.
    // v9 (B1): changedBy params added (optional, defaults null) — write one 'tags' audit row on change.
    Task<IReadOnlyList<int>> GetTagIdsAsync(int backlogId);
    Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForAllAsync();  // bulk, avoids N+1

    // P20: record that a backlog was created by "continue to next month" (traceability). Writes one
    // BacklogAudit row field='continued', new_value = the source period the copy was continued from.
    Task WriteContinuedAuditAsync(int backlogId, string? fromPeriod,
        int? changedByUserId = null, string? changedByName = null);
}
