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
    Task UpdateAsync(TaskItem task);                               // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04)
    Task SetOrderAsync(int taskId, int orderIndex);                // drag-reorder within a backlog group

    // v9 (P13-B3): task-level type/assignee update + diff-audit (assignee audited by NAME).
    Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
        int? changedByUserId = null, string? changedByName = null);
    // v9: task status change + audit (the sub-row Status dropdown path).
    Task UpdateStatusAsync(int taskId, string status,
        int? changedByUserId = null, string? changedByName = null);
    // v9 task tag links (mirror the BacklogRepository tag methods).
    Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId);
    Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
        int? changedByUserId = null, string? changedByName = null);   // audited (B3)
    Task<IReadOnlyList<TaskAuditEntry>> GetAuditAsync(int taskId);
}
