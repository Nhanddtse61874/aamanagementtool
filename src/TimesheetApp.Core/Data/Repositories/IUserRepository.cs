using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// Repository contract is VERBATIM from architecture spec §3. Implementation (UserRepository)
// is owned by P2 Task 5 (Wave 2) — this file provides only the interface so Wave-1 services
// (CurrentUserService, XC-07) can depend on it and be unit-tested with a mock.
//
// M8.2 (Wave 3-C): GetByUsernameAsync / SetUsernameAsync replace the Windows-named methods --
// schema v10 already renamed the column (Wave 1), and the app no longer identifies people by
// Windows account (the web uses username + password), so the method vocabulary caught up.
//
// row_version (v10): SetUsernameAsync / UpdateNameAsync are check-and-bump -- admin edits from
// the UI, low frequency, worth surfacing a conflict -- and throw ConcurrencyConflictException on
// a stale/missing row. SetActiveAsync (soft-delete) is bump-only: always increments, never checks.
//
// GetActiveTeamIdAsync / SetActiveTeamIdAsync (v10) read/write Users.active_team_id. Wave 4 needs
// them: ActiveTeamId currently lives in IAppConfig, which is per-PROCESS -- correct on desktop
// (one process, one user) but wrong on a server (one process serves everyone, so one user
// switching team would re-scope everyone else's data). SetActiveTeamIdAsync is a system write,
// so it is bump-only.
public interface IUserRepository
{
    Task<IReadOnlyList<User>> GetActiveAsync();
    Task<IReadOnlyList<User>> GetAllAsync();          // Users tab shows inactive too (USR-01)
    Task<User?> GetByIdAsync(int id);
    Task<User?> GetByUsernameAsync(string username);  // XC-07 lookup
    Task<int> InsertAsync(User user);                 // returns new id (USR-02)
    Task SetUsernameAsync(int userId, string username, long expectedVersion); // XC-07 persist; check-and-bump
    Task SetActiveAsync(int userId, bool isActive);                          // soft delete (USR-03); bump-only
    Task UpdateNameAsync(int userId, string name, long expectedVersion);     // check-and-bump

    Task<int> GetActiveTeamIdAsync(int userId);        // Users.active_team_id (Wave 4)
    Task SetActiveTeamIdAsync(int userId, int teamId); // bump-only (system write)
}
