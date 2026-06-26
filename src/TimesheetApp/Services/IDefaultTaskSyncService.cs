namespace TimesheetApp.Services;

// Service contract is VERBATIM from architecture spec §4. Maintains the hidden DEFAULT backlog
// and reconciles DefaultTasks -> Tasks under it (DATA-03, SET-04).
public interface IDefaultTaskSyncService
{
    Task<int> EnsureDefaultBacklogIdAsync();   // create/find backlog_code='DEFAULT' (DATA-03, idempotent)
    Task SyncAsync();                           // reconcile DefaultTasks -> Tasks under DEFAULT (SET-04)
}
