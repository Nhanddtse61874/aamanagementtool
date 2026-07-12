using Dapper;
using TimesheetApp.Models;

namespace TimesheetApp.Data.Repositories;

// User data access (USR-01..03, XC-07). SQL + Dapper only; one short connection per method.
// Soft-delete only (SetActiveAsync) — never hard-delete a user that may own TimeLogs (XC-06).
public sealed class UserRepository : IUserRepository
{
    private readonly IConnectionFactory _factory;

    public UserRepository(IConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<User>> GetActiveAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<UserRaw>(
            "SELECT id, name, windows_username, is_active FROM Users WHERE is_active = 1 ORDER BY name;");
        return rows.Select(MapUser).ToList();
    }

    public async Task<IReadOnlyList<User>> GetAllAsync()
    {
        using var c = _factory.Create();
        var rows = await c.QueryAsync<UserRaw>(
            "SELECT id, name, windows_username, is_active FROM Users ORDER BY is_active DESC, name;");
        return rows.Select(MapUser).ToList();
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<UserRaw>(
            "SELECT id, name, windows_username, is_active FROM Users WHERE id = @id;", new { id });
        return row is null ? null : MapUser(row);
    }

    public async Task<User?> GetByWindowsUsernameAsync(string windowsUsername)
    {
        using var c = _factory.Create();
        var row = await c.QuerySingleOrDefaultAsync<UserRaw>(
            "SELECT id, name, windows_username, is_active FROM Users WHERE windows_username = @w;",
            new { w = windowsUsername });
        return row is null ? null : MapUser(row);
    }

    public async Task<int> InsertAsync(User user)
    {
        using var c = _factory.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, windows_username, is_active)
              VALUES(@Name, @WindowsUsername, @IsActive);
              SELECT last_insert_rowid();",
            new { user.Name, user.WindowsUsername, IsActive = user.IsActive ? 1 : 0 });
    }

    public async Task SetWindowsUsernameAsync(int userId, string windowsUsername)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET windows_username = @w WHERE id = @id;",
            new { w = windowsUsername, id = userId });
    }

    public async Task SetActiveAsync(int userId, bool isActive)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = userId });
    }

    public async Task UpdateNameAsync(int userId, string name)
    {
        using var c = _factory.Create();
        await c.ExecuteAsync(
            "UPDATE Users SET name = @n WHERE id = @id;", new { n = name, id = userId });
    }

    private static User MapUser(UserRaw r) =>
        new((int)r.id, r.name, r.windows_username, r.is_active != 0);

    // SQLite-native shape (long/string) — narrowed at the boundary above (see Task 4 mapping note).
    private sealed class UserRaw
    {
        public long id { get; set; }
        public string name { get; set; } = "";
        public string? windows_username { get; set; }
        public long is_active { get; set; }
    }
}
