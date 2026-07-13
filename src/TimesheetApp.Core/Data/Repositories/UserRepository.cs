using Dapper;
using TimesheetApp.Data;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// User data access (USR-01..03, XC-07). SQL + Dapper only; one short connection per method.
// Soft-delete only (SetActiveAsync) — never hard-delete a user that may own TimeLogs (XC-06).
//
// Schema v10 renamed the column windows_username -> username, and Dapper binds BY COLUMN NAME,
// so the SQL below and UserRaw's property had to move with it or nothing here would read.
//
// M8.2 (Wave 3-C): the *method* vocabulary caught up with the column -- GetByWindowsUsernameAsync /
// SetWindowsUsernameAsync are now GetByUsernameAsync / SetUsernameAsync (pure rename; the app no
// longer identifies people by Windows account, the web uses username + password). The
// User.WindowsUsername model PROPERTY is a separate, much larger blast radius (28+ consumers per
// M8-PITFALL-RESEARCH) and stays out of scope here.
//
// v10 also adds row_version. SetUsername / UpdateName each come as a PAIR: the plain method is
// bump-only (always lands, always bumps, never throws), the *CheckedAsync sibling is check-and-bump
// and returns the new row_version. SetActiveAsync (soft-delete) and SetActiveTeamIdAsync (system
// write, Wave 4) are bump-only with no checked sibling: always increment, never compare.
public sealed class UserRepository : IUserRepository
{
    private readonly IConnectionFactory _factory;

    public UserRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<User>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<UserRaw>(
            "SELECT id, name, username, is_active, is_admin, row_version FROM Users WHERE is_active = 1 ORDER BY name;");
        return rows.Select(MapUser).ToList();
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<UserRaw>(
            "SELECT id, name, username, is_active, is_admin, row_version FROM Users ORDER BY is_active DESC, name;");
        return rows.Select(MapUser).ToList();
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<UserRaw>(
            "SELECT id, name, username, is_active, is_admin, row_version FROM Users WHERE id = @id;", new { id });
        return row is null ? null : MapUser(row);
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<UserRaw>(
            "SELECT id, name, username, is_active, is_admin, row_version FROM Users WHERE username = @w;",
            new { w = username });
        return row is null ? null : MapUser(row);
    }

    public async Task<int> InsertAsync(User user)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, username, is_active)
              VALUES(@Name, @WindowsUsername, @IsActive);
              SELECT last_insert_rowid();",
            new { user.Name, user.WindowsUsername, IsActive = user.IsActive ? 1 : 0 });
    }

    // BUMP-ONLY (XC-07 persist). Claiming your own Windows identity is a system write, not a contested
    // user edit: CurrentUserService runs this when Current is null, so it holds no version to check
    // against, and there is nobody to race — nobody else is claiming your account.
    public async Task SetUsernameAsync(int userId, string username)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET username = @w, row_version = row_version + 1 WHERE id = @id;",
            new { w = username, id = userId });
    }

    // CHECK-AND-BUMP: admin-driven UI edit, low frequency, worth surfacing a conflict.
    public async Task<long> SetUsernameCheckedAsync(int userId, string username, long expectedVersion)
    {
        using var c = _factory.Create();
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE Users SET username = @w, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected
              RETURNING row_version;",
            new { w = username, id = userId, expected = expectedVersion });
        if (newVersion is not null) return newVersion.Value;

        var exists = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM Users WHERE id = @id;", new { id = userId });
        throw new ConcurrencyConflictException("Users", userId, expectedVersion, deleted: exists == 0);
    }

    // Bump-only: soft-delete is a system write, carries no client-observed version.
    public async Task SetActiveAsync(int userId, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET is_active = @a, row_version = row_version + 1 WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = userId });
    }

    // BUMP-ONLY: always lands, always bumps, never throws (the Settings rename path).
    public async Task UpdateNameAsync(int userId, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET name = @n, row_version = row_version + 1 WHERE id = @id;",
            new { n = name, id = userId });
    }

    // CHECK-AND-BUMP: admin-driven UI edit, low frequency, worth surfacing a conflict.
    public async Task<long> UpdateNameCheckedAsync(int userId, string name, long expectedVersion)
    {
        using var c = _factory.Create();
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE Users SET name = @n, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected
              RETURNING row_version;",
            new { n = name, id = userId, expected = expectedVersion });
        if (newVersion is not null) return newVersion.Value;

        var exists = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM Users WHERE id = @id;", new { id = userId });
        throw new ConcurrencyConflictException("Users", userId, expectedVersion, deleted: exists == 0);
    }

    // Users.active_team_id (v10). Read side of the accessor pair Wave 4 depends on: it moves
    // ActiveTeamId out of IAppConfig (per-process -- wrong on a server, where one process serves
    // everyone) and into here (per-user). See IUserRepository for the fuller why.
    public async Task<int> GetActiveTeamIdAsync(int userId)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            "SELECT active_team_id FROM Users WHERE id = @id;", new { id = userId });
    }

    // Bump-only: system write, carries no client-observed version.
    public async Task SetActiveTeamIdAsync(int userId, int teamId)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET active_team_id = @t, row_version = row_version + 1 WHERE id = @id;",
            new { t = teamId, id = userId });
    }

    // --- M8.3: credentials (Users.password_hash / is_admin) ---

    // Deliberately does NOT filter on is_active: it projects is_active instead, so the auth path can tell
    // "no such user" (null) apart from "deactivated user" (IsActive == false) and choose what to say.
    public async Task<UserCredentials?> GetCredentialsAsync(string username)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<CredentialsRaw>(
            "SELECT id, name, password_hash, is_admin, is_active FROM Users WHERE username = @u;",
            new { u = username });
        return row is null
            ? null
            : new UserCredentials((int)row.id, row.name, row.password_hash, row.is_admin != 0, row.is_active != 0);
    }

    // BUMP-ONLY: a password change carries no client-held version and has nobody to race.
    public async Task SetPasswordHashAsync(int userId, string hash)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET password_hash = @h, row_version = row_version + 1 WHERE id = @id;",
            new { h = hash, id = userId });
    }

    // CHECK-AND-BUMP (M9): identical shape to UpdateNameCheckedAsync — the write lands only at
    // expectedVersion, RETURNING hands back the new version from the same statement (a read-back would be
    // racy), and a miss is disambiguated into stale-vs-deleted by the COUNT before throwing.
    //
    // Checked rather than bump-only because this is a PRIVILEGE change: two admins racing on one user's
    // row must not silently lose one of the two decisions. See IUserRepository for the naming note.
    public async Task<long> SetIsAdminAsync(int userId, bool isAdmin, long expectedVersion)
    {
        using var c = _factory.Create();
        var newVersion = await c.QuerySingleOrDefaultAsync<long?>(
            @"UPDATE Users SET is_admin = @a, row_version = row_version + 1
              WHERE id = @id AND row_version = @expected
              RETURNING row_version;",
            new { a = isAdmin ? 1 : 0, id = userId, expected = expectedVersion });
        if (newVersion is not null) return newVersion.Value;

        var exists = await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM Users WHERE id = @id;", new { id = userId });
        throw new ConcurrencyConflictException("Users", userId, expectedVersion, deleted: exists == 0);
    }

    // ATOMIC CLAIM. The `WHERE password_hash IS NULL` makes the check and the write one statement, so two
    // processes racing a bootstrap (an overlapped service restart) produce ONE winner and one no-op rather
    // than two passwords. It is also what stops bootstrap from ever overwriting a password that already
    // exists: a user with a hash simply matches no row, and this returns false.
    public async Task<bool> TryBootstrapAdminPasswordAsync(int userId, string hash)
    {
        using var c = _factory.Create();
        var rows = await c.ExecuteAsync(
            @"UPDATE Users SET password_hash = @h, row_version = row_version + 1
               WHERE id = @id AND password_hash IS NULL;",
            new { h = hash, id = userId });
        return rows == 1;
    }

    private static User MapUser(UserRaw r) =>
        new((int)r.id, r.name, r.username, r.is_active != 0, r.row_version, r.is_admin != 0);

    // SQLite-native shape (long/string) — narrowed at the boundary above (see Task 4 mapping note).
    private sealed class UserRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public string? username { get; set; }   // binds to Users.username (v10 rename)
        public long is_active { get; set; }
        public long is_admin { get; set; }      // v10; projected so /api/users can show who is an admin
        public long row_version { get; set; }
    }

    // SQLite-native shape for the credential read. password_hash is nullable (a user who has never had
    // a password set); is_admin/is_active are INTEGER 0/1.
    private sealed class CredentialsRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public string? password_hash { get; set; }
        public long is_admin { get; set; }
        public long is_active { get; set; }
    }
}
