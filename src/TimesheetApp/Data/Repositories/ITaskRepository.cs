using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (TaskRepository)
// is owned by P2 Task 5 (Wave 2). Template methods live on ITaskTemplateRepository (P4), NOT here.
public interface ITaskRepository
{
    Task<IReadOnlyList<TaskItem>> GetActiveByBacklogAsync(int backlogId);
    Task<IReadOnlyList<TaskItem>> GetActiveForTimesheetAsync();    // active tasks across active backlogs + DEFAULT, ordered (TS-02)
    Task<TaskItem?> GetByIdAsync(int id);
    Task<TaskItem?> GetByNameInBacklogAsync(int backlogId, string taskName); // DEFAULT sync match by name (DATA-03/SET-04)
    Task<int> InsertAsync(TaskItem task);                          // REQ-02/REQ-03/SET-04
    Task UpdateAsync(TaskItem task);                               // name + order_index
    Task SetActiveAsync(int taskId, bool isActive);                // soft delete (REQ-04/SET-04)
    Task SetOrderAsync(int taskId, int orderIndex);                // drag-reorder within a backlog group
}
