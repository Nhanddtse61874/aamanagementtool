namespace TimesheetApp.Services;

// Service contract is VERBATIM from architecture spec §4. Maintains the hidden DEFAULT backlog
// and reconciles DefaultTasks -> Tasks under it (DATA-03, SET-04).
public interface IDefaultTaskSyncService
{
    // P10 (TM-04): DEFAULT is unique per team. Create/find backlog_code='DEFAULT' for THIS team.
    Task<int> EnsureDefaultBacklogIdAsync(int teamId);  // idempotent per team (DATA-03)
    Task SyncAsync();                                    // reconcile global DefaultTasks -> Tasks under EACH active team's DEFAULT (SET-04)
}
