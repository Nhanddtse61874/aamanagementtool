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
    //
    // M8.2 optimistic concurrency. expectedVersion is the row_version the caller last read
    // (GetRowVersionAsync):
    //   non-null => check-and-bump: the write only lands if row_version still matches, else
    //               ConcurrencyConflictException. This is the path a user edit takes.
    //   null     => bump-only: the write always lands and still increments row_version, and never
    //               throws. System writes and the WPF call sites (which carry no version) take this.
    // Either way row_version is bumped -- bumping without checking is safe, checking without
    // bumping is the lost update this exists to prevent.
    Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null,
        string? auditNote = null, long? expectedVersion = null);
    // Current row_version, or null when the backlog is gone. A caller reads this, lets the user edit,
    // then hands it back as expectedVersion -- the stale number IS the conflict window.
    Task<long?> GetRowVersionAsync(int backlogId);
    Task<IReadOnlyList<BacklogAuditEntry>> GetAuditAsync(int backlogId);   // v2 change history
    // Batch form of GetAuditAsync: one IN-query across many backlogs, grouped by backlog_id (avoids
    // N+1). Each group is ordered id DESC like the single-backlog query; absent ids are missing keys.
    Task<IReadOnlyDictionary<int, IReadOnlyList<BacklogAuditEntry>>> GetAuditForBacklogsAsync(IReadOnlyList<int> backlogIds);
    // No SetActiveAsync — Backlogs are NOT soft-deletable (decision 4).

    // v7 tag links (TAG-02). SetTagsAsync replaces the whole set for one backlog in a single tx.
    // v9 (B1): changedBy params added (optional, defaults null) — write one 'tags' audit row on change.
    // M8.2: BacklogTags is a link table and carries no row_version of its own, so the version this
    // replace-all checks and bumps is the PARENT backlog's — the chips are part of the card the user
    // is looking at, and two people re-ticking them race exactly like any other inline edit.
    Task<IReadOnlyList<int>> GetTagIdsAsync(int backlogId);
    Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForAllAsync();  // bulk, avoids N+1

    // P20: record that a backlog was created by "continue to next month" (traceability). Writes one
    // BacklogAudit row field='continued', new_value = the source period the copy was continued from.
    Task WriteContinuedAuditAsync(int backlogId, string? fromPeriod,
        int? changedByUserId = null, string? changedByName = null);
}
