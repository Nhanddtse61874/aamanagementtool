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
    // ---- M8.2 optimistic concurrency: two methods, not one optional parameter -------------------
    //
    // Every versioned write comes in a pair. The plain method is BUMP-ONLY: it always lands, always
    // increments row_version, and never throws. It is what a system write takes -- and what every
    // existing WPF call site takes, since none of them carries a version.
    //
    // The *CheckedAsync sibling is CHECK-AND-BUMP: the write lands only while row_version still
    // equals expectedVersion, otherwise ConcurrencyConflictException. It RETURNS the new row_version,
    // and that return is the point of the whole shape. A void checked write would force the caller to
    // re-read the version for its next save, and that read-back is racy: between the write committing
    // and the re-read, someone else can write. The caller would pick up THEIR version while still
    // holding ITS OWN data, and the next save would pass the check and silently overwrite them --
    // precisely the lost update this mechanism exists to prevent. Returning the version from the same
    // statement that performed the write (RETURNING row_version) closes that window by construction.
    //
    // The version travels ONLY as this explicit argument. It is never read off Backlog.RowVersion --
    // RequestsViewModel.SaveEditAsync builds a FRESH Backlog from editor fields, so its RowVersion is
    // the default 0, and a write that trusted the record would reject every edit in the app.
    //
    // (There is no GetRowVersionAsync. The version now arrives on the entity from the SELECT, so a
    // separate round-trip to fetch it would be a second read of the same row -- and one that could
    // disagree with the first.)

    // Edit name/project + v2 start/end/month/type. changedBy is recorded in BacklogAudit when the
    // audited fields change. v9 (B2): auditNote is stored in BacklogAudit.note for deadline fields only.
    Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null,
        string? auditNote = null);
    /// <returns>The row_version AFTER the write — the caller's next expectedVersion.</returns>
    /// <exception cref="ConcurrencyConflictException">Version moved on, or the row is gone.</exception>
    Task<long> UpdateCheckedAsync(Backlog backlog, long expectedVersion,
        int? changedByUserId = null, string? changedByName = null, string? auditNote = null);
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
        int? changedByUserId = null, string? changedByName = null);
    /// <returns>The PARENT backlog's row_version after the write.</returns>
    /// <exception cref="ConcurrencyConflictException">Version moved on, or the backlog is gone.</exception>
    Task<long> SetTagsCheckedAsync(int backlogId, IReadOnlyList<int> tagIds, long expectedVersion,
        int? changedByUserId = null, string? changedByName = null);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetTagIdsForAllAsync();  // bulk, avoids N+1

    // P20: record that a backlog was created by "continue to next month" (traceability). Writes one
    // BacklogAudit row field='continued', new_value = the source period the copy was continued from.
    Task WriteContinuedAuditAsync(int backlogId, string? fromPeriod,
        int? changedByUserId = null, string? changedByName = null);
}
