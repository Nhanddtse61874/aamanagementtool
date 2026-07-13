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
// row_version (v10): every versioned write comes as a PAIR (see IBacklogRepository for the full
// rationale). <Verb>Async is BUMP-ONLY -- always lands, always bumps, never throws. <Verb>CheckedAsync
// is CHECK-AND-BUMP: it lands only at expectedVersion, throws ConcurrencyConflictException on a
// stale/missing row, and RETURNS the new row_version so the caller never re-reads it (a read-back is
// racy). SetActiveAsync (soft-delete) is bump-only with no checked sibling.
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
    Task SetUsernameAsync(int userId, string username);                      // XC-07 persist; bump-only
    Task<long> SetUsernameCheckedAsync(int userId, string username, long expectedVersion);
    Task SetActiveAsync(int userId, bool isActive);                          // soft delete (USR-03); bump-only
    Task UpdateNameAsync(int userId, string name);                           // bump-only
    Task<long> UpdateNameCheckedAsync(int userId, string name, long expectedVersion);

    Task<int> GetActiveTeamIdAsync(int userId);        // Users.active_team_id (Wave 4)
    Task SetActiveTeamIdAsync(int userId, int teamId); // bump-only (system write)

    // --- M8.3: credentials (Users.password_hash / is_admin, added by v10 and until now unread) ---

    /// <summary>Resolves the login surface for a username. Returns <c>null</c> when no such user exists.
    /// A user with a NULL <see cref="UserCredentials.PasswordHash"/> has never had a password set — the
    /// caller must treat that as "cannot log in", never as "any password matches".</summary>
    Task<UserCredentials?> GetCredentialsAsync(string username);

    /// <summary>Sets (or replaces) a user's password hash. BUMP-ONLY: a password change carries no
    /// client-held row_version to check against, and nobody else is racing to set your password.</summary>
    Task SetPasswordHashAsync(int userId, string hash);

    /// <summary>Atomically claims the password slot of a user who has none: a single
    /// <c>UPDATE ... WHERE password_hash IS NULL</c>, so the check and the write cannot be split.
    /// Returns <c>true</c> only for the caller that actually landed the write.
    ///
    /// The WHERE clause is load-bearing. Startup bootstrap is the one place two processes genuinely race
    /// (an overlapped service restart runs it twice): a read-then-write would let both see NULL and both
    /// write, leaving the admin with whichever password lost the race and the operator holding the other.
    /// This produces one winner and one no-op instead. It is also why bootstrap can never silently
    /// OVERWRITE an existing password — a user with a hash is simply not matched.</summary>
    Task<bool> TryBootstrapAdminPasswordAsync(int userId, string hash);
}
