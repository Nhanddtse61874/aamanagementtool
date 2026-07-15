using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// Schema v11 (unique, case-insensitive Users.username). The migration adds ONE thing:
//
//     CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username
//         ON Users(username COLLATE NOCASE) WHERE username IS NOT NULL;
//
// The bug it closes: username was born as windows_username TEXT in the v1 DDL and only RENAMED by v10, so
// it never had a unique constraint. Two rows sharing a username made GetCredentialsAsync/GetByUsernameAsync
// (QuerySingleOrDefault over `WHERE username = @u`) THROW -> a 500 on login that locked BOTH users out.
//
// The headline test migrates a fixture built to MIRROR THE REAL PRODUCTION DATABASE at the moment this
// ships: user_version = 10, 12 users, exactly ONE non-null username ('Admin'), 11 NULLs. That shape has no
// NOCASE collision, so the index is safe to add -- and this proves it against that exact shape, on a COPY,
// without ever touching the real file.
public class SchemaV11MigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;
    private readonly DatabaseInitializer _sut;

    public SchemaV11MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-v11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
        _sut = new DatabaseInitializer(_factory);
    }

    private static bool IndexExists(IDbConnection c, string name) =>
        c.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@n;", new { n = name }) == 1;

    // ---- v10 fixture: the real database's shape ------------------------------------------------
    //
    // A full v10 schema (post-v6 renames, row_version on the 8 versioned tables, the v10 auth/scope columns
    // on Users), at PRAGMA user_version = 10, seeded EXACTLY like the live database: user #1 'Admin' is the
    // only non-null username and the only admin (with a password hash to prove it survives); users #2..#12
    // have a NULL username. Only the tables the v11 step + post-migration seeding actually touch are written
    // by hand -- CreateTables (IF NOT EXISTS) supplies any it does not find, and none of those affect v11.
    private void SeedV10Database()
    {
        using var c = _factory.Create();

        c.Execute(@"
CREATE TABLE Users (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT    NOT NULL,
    username       TEXT,
    is_active      INTEGER NOT NULL DEFAULT 1,
    row_version    INTEGER NOT NULL DEFAULT 1,
    password_hash  TEXT,
    is_admin       INTEGER NOT NULL DEFAULT 0,
    active_team_id INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE Backlogs (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_code TEXT    NOT NULL,
    project      TEXT    NOT NULL,
    created_at   TEXT    NOT NULL,
    type         TEXT,
    team_id      INTEGER,
    row_version  INTEGER NOT NULL DEFAULT 1
);

PRAGMA user_version = 10;");

        // user #1 is the live database's single non-null username, single admin, with a password.
        c.Execute(
            "INSERT INTO Users(id, name, username, is_active, is_admin, password_hash) " +
            "VALUES(1, 'Admin', 'Admin', 1, 1, @hash);",
            new { hash = "PBKDF2$fixture$adminhash" });

        // users #2..#12: the 11 real users with a NULL username (they never logged into the web).
        for (var id = 2; id <= 12; id++)
            c.Execute(
                "INSERT INTO Users(id, name, username, is_active) VALUES(@id, @name, NULL, 1);",
                new { id, name = $"User {id}" });
    }

    [Fact]
    public async Task Migration_v11_adds_the_unique_username_index_and_preserves_every_user()
    {
        SeedV10Database();

        using (var before = _factory.Create())
        {
            Assert.Equal(10, before.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.False(IndexExists(before, "ux_users_username")); // genuinely absent before v11
            Assert.Equal(12, before.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));
        }

        await _sut.InitializeAsync();

        using (var c = _factory.Create())
        {
            // 1. Version advanced to 11.
            Assert.Equal(11, c.ExecuteScalar<long>("PRAGMA user_version;"));

            // 2. The index exists.
            Assert.True(IndexExists(c, "ux_users_username"), "ux_users_username was not created.");

            // 3. All 12 users survived -- a migration against real data must lose nobody.
            Assert.Equal(12, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));

            // 4. The Admin row is intact, with its username, its admin flag, and its password hash.
            var admin = c.QuerySingle<(string username, long is_admin, string? hash)>(
                "SELECT username, is_admin, password_hash AS hash FROM Users WHERE id = 1;");
            Assert.Equal("Admin", admin.username);
            Assert.Equal(1, admin.is_admin);
            Assert.Equal("PBKDF2$fixture$adminhash", admin.hash);

            // The 11 NULL usernames are still NULL -- the partial index left them untouched and legal.
            Assert.Equal(11, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE username IS NULL;"));
        }

        // 5. Running it again is a no-op: version stays 11, no error, every user still present.
        await _sut.InitializeAsync();

        using (var c = _factory.Create())
        {
            Assert.Equal(11, c.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.True(IndexExists(c, "ux_users_username"));
            Assert.Equal(12, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));
        }
    }

    [Fact] // NOCASE: 'Nhan' and 'nhan' are the SAME username, so the second insert is refused at the DB.
    public async Task Two_users_cannot_share_a_username_even_differing_only_in_CASE()
    {
        await _sut.InitializeAsync(); // fresh -> straight to v11, index present

        using var c = _factory.Create();
        c.Execute("INSERT INTO Users(name, username, is_active) VALUES('First', 'Nhan', 1);");

        var ex = Assert.ThrowsAny<SqliteException>(() =>
            c.Execute("INSERT INTO Users(name, username, is_active) VALUES('Second', 'nhan', 1);"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly one 'Nhan'-ish row exists; the collision did not sneak in.
        Assert.Equal(1, c.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM Users WHERE username = 'Nhan' COLLATE NOCASE;"));
    }

    [Fact] // The 11 real NULL-username users depend on this: NULLs are distinct in the partial index.
    public async Task Multiple_users_may_still_have_a_NULL_username()
    {
        await _sut.InitializeAsync(); // fresh -> v11

        using var c = _factory.Create();
        c.Execute(@"
INSERT INTO Users(name, username, is_active) VALUES('A', NULL, 1);
INSERT INTO Users(name, username, is_active) VALUES('B', NULL, 1);
INSERT INTO Users(name, username, is_active) VALUES('C', NULL, 1);");

        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE username IS NULL;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp file occasionally held briefly on Windows; safe to leave for the OS to reap.
        }
    }
}
