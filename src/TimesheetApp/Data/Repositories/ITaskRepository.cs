using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (TaskRepository)
// is owned by P2 Task 5 (Wave 2). Template methods live on ITaskTemplateRepository (P4), NOT here.
public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByBacklogAsync(int backlogId);
    // P10 (TM-06): the Log Work grid is active-team-scoped. teamId null => all teams (preserves
    // existing tests); a teamId filters via the backlog's team_id; teamId 0 => no tasks (empty, R6).
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync(int? teamId = null);    // active tasks for the team's backlogs + DEFAULT, ordered (TS-02)
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInBacklogAsync(int backlogId, string taskName); // DEFAULT sync match by name (DATA-03/SET-04)
    Task<int> InsertAsync(TaskItem task);                          // REQ-02/REQ-03/SET-04
    Task UpdateAsync(TaskItem task);                               // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04)
    Task SetOrderAsync(int taskId, int orderIndex);                // drag-reorder within a backlog group
}
