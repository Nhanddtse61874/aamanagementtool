using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// DefaultTasks data access — the read/write seam DefaultTaskSyncService reconciles into
// Tasks under the hidden DEFAULT request (SET-04). SQL + Dapper only, soft-delete via
// SetActiveAsync (never hard-delete; a hidden default's Task may own TimeLogs — XC-06).
public interface IDefaultTaskRepository
{
    Task<IReadOnlyList<DefaultTask>> GetActiveAsync();   // active DefaultTasks, ordered (SET-04 source set)
    Task<int> InsertAsync(DefaultTask defaultTask);      // add a default-task row (SET-04)
    Task SetActiveAsync(int id, bool isActive);          // hide/show a default task (DATA-04/SET-04)
}
