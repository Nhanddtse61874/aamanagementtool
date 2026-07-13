using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (TaskRepository)
// is owned by P2 Task 5 (Wave 2). Template methods live on ITaskTemplateRepository (P4), NOT here.
public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByBacklogAsync(int backlogId);
    // Batch form of GetActiveByBacklogAsync: one IN-query for many backlogs, grouped by backlog_id
    // (avoids N+1). Absent backlog ids are simply missing keys (callers index via TryGetValue).
    Task<IReadOnlyDictionary<int, IReadOnlyList<TaskItem>>> GetActiveByBacklogsAsync(IReadOnlyList<int> backlogIds);
    // P10 (TM-06): the Log Work grid is active-team-scoped. teamId null => all teams (preserves
    // existing tests); a teamId filters via the backlog's team_id; teamId 0 => no tasks (empty, R6).
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync(int? teamId = null);    // active tasks for the team's backlogs + DEFAULT, ordered (TS-02)
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInBacklogAsync(int backlogId, string taskName); // DEFAULT sync match by name (DATA-03/SET-04)
    Task<int> InsertAsync(TaskItem task);                          // REQ-02/REQ-03/SET-04

    // ---- M8.2 optimistic concurrency ---------------------------------------------------------
    // Every versioned write comes as a PAIR (see IBacklogRepository for the full rationale):
    //   <Verb>Async        -> BUMP-ONLY.     Always lands, always bumps row_version, never throws.
    //   <Verb>CheckedAsync -> CHECK-AND-BUMP. Lands only at expectedVersion, else throws; RETURNS the
    //                        new row_version, so the caller never re-reads it (a read-back is racy).
    // The version travels only as the explicit argument — never read off TaskItem.RowVersion.
    //
    // SetActiveAsync and SetOrderAsync are bump-only with NO checked sibling, by design. SetOrderAsync
    // in particular runs ONCE PER ROW during a drag (TimesheetViewModel.ReorderAsync loops it over the
    // whole list): under check-and-bump the first row would bump the version and every later row would
    // arrive stale, so an ordinary drag-and-drop would 409-storm. A reorder carries no version from a
    // client — it is a system write.
    //
    // (There is no GetRowVersionAsync: the version now arrives on TaskItem from the SELECT.)

    Task UpdateAsync(TaskItem task);                               // name + order_index + status
    Task<long> UpdateCheckedAsync(TaskItem task, long expectedVersion);
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04) — bump-only
    Task SetOrderAsync(int taskId, int orderIndex);                // drag-reorder — bump-only, see above

    // v9 (P13-B3): task-level type/assignee update + diff-audit (assignee audited by NAME).
    Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
        int? changedByUserId = null, string? changedByName = null);
    Task<long> UpdateExtendedCheckedAsync(int taskId, string? type, int? assigneeUserId, long expectedVersion,
        int? changedByUserId = null, string? changedByName = null);
    // v9: task status change + audit (the sub-row Status dropdown path).
    Task UpdateStatusAsync(int taskId, string status,
        int? changedByUserId = null, string? changedByName = null);
    Task<long> UpdateStatusCheckedAsync(int taskId, string status, long expectedVersion,
        int? changedByUserId = null, string? changedByName = null);
    // v9 task tag links (mirror the BacklogRepository tag methods). TaskTags carries no row_version,
    // so the version checked/bumped is the PARENT task's.
    Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId);
    Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null);   // audited (B3)
    Task<long> SetTaskTagsCheckedAsync(int taskId, IReadOnlyList<int> tagIds, long expectedVersion,
        int? changedByUserId = null, string? changedByName = null);
    Task<IReadOnlyList<TaskAuditEntry>> GetAuditAsync(int taskId);
}
