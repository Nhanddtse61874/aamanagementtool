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
    // expectedVersion non-null => check-and-bump (user edit; ConcurrencyConflictException on a stale
    // version). null => bump-only (never throws). BOTH always bump row_version.
    //
    // SetActiveAsync and SetOrderAsync are bump-only by design and take no expectedVersion at all.
    // SetOrderAsync in particular runs ONCE PER ROW during a drag (TimesheetViewModel.ReorderAsync
    // loops it over the whole list): under check-and-bump the first row would bump the version and
    // every later row would arrive stale, so an ordinary drag-and-drop would 409-storm. A reorder
    // carries no version from a client — it is a system write.

    Task UpdateAsync(TaskItem task, long? expectedVersion = null);  // name + order_index + status
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04) — bump-only
    Task SetOrderAsync(int taskId, int orderIndex);                // drag-reorder — bump-only, see above

    // Current row_version, or null when the task is gone (see IBacklogRepository.GetRowVersionAsync).
    Task<long?> GetRowVersionAsync(int taskId);

    // v9 (P13-B3): task-level type/assignee update + diff-audit (assignee audited by NAME).
    Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null);
    // v9: task status change + audit (the sub-row Status dropdown path).
    Task UpdateStatusAsync(int taskId, string status,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null);
    // v9 task tag links (mirror the BacklogRepository tag methods). TaskTags carries no row_version,
    // so the version checked/bumped is the PARENT task's.
    Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId);
    Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null, long? expectedVersion = null);   // audited (B3)
    Task<IReadOnlyList<TaskAuditEntry>> GetAuditAsync(int taskId);
}
