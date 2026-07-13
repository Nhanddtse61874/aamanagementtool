using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// Schema v10 (M8.2 wave 1): row_version on the 8 concurrently-editable tables + auth/scope
// columns on Users. This is a migration against a database that holds REAL DATA, so the tests
// below exercise the two paths that actually exist in the wild:
//
//   1. UPGRADE  -- a v9 database with rows in it is migrated in place (data must survive).
//   2. FRESH    -- no database file at all; CreateTables + every migration step replays from 0.
//
// Path 2 is the one that silently rots: RunMigrations replays EVERY step on a new database, so
// CreateTables' DDL is the *v1* schema. If someone "tidies" CreateTables' windows_username to
// username, existing installs keep working and only fresh installs break. FreshInstall_* is the
// regression guard for exactly that.
public class SchemaV10MigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;
    private readonly DatabaseInitializer _sut;

    // The 8 tables that gain row_version. Two users can reach the same row on each of these.
    private static readonly string[] VersionedTables =
    {
        "Backlogs", "Tasks", "TimeLogs", "StandupIssues", "Users", "Teams", "Tags", "PcaContacts"
    };

    // Deliberately NOT versioned (spec §7.1). Holidays/Settings are key/date-keyed -- last-write-wins
    // IS the correct semantics. StandupEntries is owner-gated inside StandupService, so two users
    // cannot reach the same row to begin with. Asserted so a later "helpful" addition has to be a
    // decision rather than a reflex.
    private static readonly string[] UnversionedTables = { "Holidays", "Settings", "StandupEntries" };

    public SchemaV10MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-v10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
        _sut = new DatabaseInitializer(_factory);
    }

    private static List<string> Columns(IDbConnection c, string table) =>
        c.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    // ---- v9 fixture -----------------------------------------------------------------------
    //
    // Builds a database in the state a real v9 install is actually in, then seeds it with rows.
    // Only the four tables whose v9 shape DIFFERS from CreateTables' v1 baseline are written out
    // by hand (Backlogs/BacklogAudit exist only under their post-v6 names; Tasks and StandupEntries
    // carry the v6/v8/v9 renames+columns). Everything else -- Users, TimeLogs, StandupIssues, Teams,
    // Tags, PcaContacts, ... -- has been shape-stable since it was introduced, so CreateTables
    // produces the correct v9 shape for it and we let it. Users in particular is v1-shaped at v9,
    // which is the whole reason the v10 rename has a windows_username to rename.
    private void SeedV9Database()
    {
        using var c = _factory.Create();

        c.Execute(@"
CREATE TABLE Users (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT    NOT NULL,
    windows_username TEXT,
    is_active        INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE Backlogs (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_code            TEXT    NOT NULL,
    project                 TEXT    NOT NULL,
    created_at              TEXT    NOT NULL,
    start_date              TEXT,
    end_date                TEXT,
    period_month            TEXT,
    type                    TEXT,
    assignee_user_id        INTEGER,
    deadline_internal       TEXT,
    deadline_external       TEXT,
    rough_estimate_hours    REAL,
    official_estimate_hours REAL,
    progress_percent        INTEGER,
    note                    TEXT,
    pca_contact_id          INTEGER,
    team_id                 INTEGER
);

CREATE TABLE BacklogAudit (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_id         INTEGER NOT NULL,
    field              TEXT    NOT NULL,
    old_value          TEXT,
    new_value          TEXT,
    changed_by_user_id INTEGER,
    changed_by_name    TEXT,
    changed_at         TEXT    NOT NULL,
    note               TEXT
);

CREATE TABLE Tasks (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_id       INTEGER NOT NULL,
    task_name        TEXT    NOT NULL,
    order_index      INTEGER NOT NULL DEFAULT 0,
    is_active        INTEGER NOT NULL DEFAULT 1,
    status           TEXT    NOT NULL DEFAULT 'Todo',
    type             TEXT,
    assignee_user_id INTEGER,
    FOREIGN KEY (backlog_id) REFERENCES Backlogs(id)
);

CREATE TABLE TimeLogs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL,
    task_id    INTEGER NOT NULL,
    work_date  TEXT    NOT NULL,
    hours      REAL    NOT NULL,
    created_at TEXT    NOT NULL,
    FOREIGN KEY (user_id) REFERENCES Users(id),
    FOREIGN KEY (task_id) REFERENCES Tasks(id),
    UNIQUE (user_id, task_id, work_date)
);

CREATE TABLE StandupEntries (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id      INTEGER NOT NULL,
    work_date    TEXT    NOT NULL,
    section      TEXT    NOT NULL,
    backlog_id   INTEGER,
    backlog_code TEXT    NOT NULL,
    task_text    TEXT    NOT NULL,
    description  TEXT    NOT NULL DEFAULT '',
    deadline     TEXT,
    status       TEXT    NOT NULL,
    order_index  INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT    NOT NULL,
    team_id      INTEGER,
    FOREIGN KEY (user_id) REFERENCES Users(id)
);

CREATE TABLE StandupIssues (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id      INTEGER NOT NULL,
    issue_text    TEXT    NOT NULL,
    solution_text TEXT,
    status        TEXT    NOT NULL DEFAULT 'open',
    order_index   INTEGER NOT NULL DEFAULT 0,
    created_at    TEXT    NOT NULL,
    FOREIGN KEY (entry_id) REFERENCES StandupEntries(id) ON DELETE CASCADE
);

CREATE TABLE Teams (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,
    is_active  INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL
);

CREATE TABLE Tags (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    text       TEXT    NOT NULL,
    icon       TEXT    NOT NULL,
    color      TEXT    NOT NULL,
    created_at TEXT    NOT NULL
);

CREATE TABLE PcaContacts (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    name      TEXT    NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1
);

PRAGMA user_version = 9;");

        // Real-looking rows. Users get bare usernames -- exactly what windows_username already holds,
        // which is why the v10 rename is not a data migration.
        const string now = "2026-07-01T09:00:00Z";
        c.Execute(@"
INSERT INTO Users(id, name, windows_username, is_active) VALUES
    (1, 'Nhan',  'nhan',    1),
    (2, 'Chi',   'chi.le',  1),
    (3, 'Retired', NULL,    0);

INSERT INTO Teams(id, name, is_active, created_at) VALUES (1, 'Architect Improvement', 1, @now);

INSERT INTO Backlogs(id, backlog_code, project, created_at, type, progress_percent, team_id)
VALUES (1, 'REQ-900', 'ARCS', @now, 'Bug', 40, 1);

INSERT INTO Tasks(id, backlog_id, task_name, order_index, is_active, status)
VALUES (1, 1, 'Implement login', 0, 1, 'Todo');

INSERT INTO TimeLogs(id, user_id, task_id, work_date, hours, created_at)
VALUES (1, 1, 1, '2026-07-01', 7.5, @now);

INSERT INTO StandupEntries(id, user_id, work_date, section, backlog_id, backlog_code, task_text, status, created_at, team_id)
VALUES (1, 1, '2026-07-01', 'Today', 1, 'REQ-900', 'Finish login', 'InProgress', @now, 1);

INSERT INTO StandupIssues(id, entry_id, issue_text, status, created_at)
VALUES (1, 1, 'Blocked on API key', 'open', @now);

INSERT INTO Tags(id, text, icon, color, created_at) VALUES (1, 'urgent', 'fire', '#f00', @now);

INSERT INTO PcaContacts(id, name, is_active) VALUES (1, 'Mr. Tanaka', 1);", new { now });
    }

    // ---- Upgrade path: v9 -> v10 -----------------------------------------------------------

    [Fact]
    public async Task Upgrade_From_V9_Lands_On_Version_10()
    {
        SeedV9Database();
        using (var before = _factory.Create())
            Assert.Equal(9, before.ExecuteScalar<long>("PRAGMA user_version;"));

        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));
    }

    [Fact] // The whole point: this migration runs against a database with real data in it.
    public async Task Upgrade_From_V9_Preserves_Existing_Rows()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));
        Assert.Equal(7.5, c.ExecuteScalar<double>("SELECT hours FROM TimeLogs WHERE id = 1;"));
        Assert.Equal("REQ-900", c.ExecuteScalar<string>("SELECT backlog_code FROM Backlogs WHERE id = 1;"));
        Assert.Equal("Implement login", c.ExecuteScalar<string>("SELECT task_name FROM Tasks WHERE id = 1;"));
        Assert.Equal("Blocked on API key", c.ExecuteScalar<string>("SELECT issue_text FROM StandupIssues WHERE id = 1;"));
        Assert.Equal("Architect Improvement", c.ExecuteScalar<string>("SELECT name FROM Teams WHERE id = 1;"));
        Assert.Equal("urgent", c.ExecuteScalar<string>("SELECT text FROM Tags WHERE id = 1;"));
        Assert.Equal("Mr. Tanaka", c.ExecuteScalar<string>("SELECT name FROM PcaContacts WHERE id = 1;"));
        // The standup entry survives even though it is deliberately not versioned.
        Assert.Equal("Finish login", c.ExecuteScalar<string>("SELECT task_text FROM StandupEntries WHERE id = 1;"));
    }

    [Fact] // A rename, not a data migration: `nhan` is still `nhan`, so nobody's login or history moves.
    public async Task Upgrade_From_V9_Renames_Column_Preserving_Its_Values()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        var cols = Columns(c, "Users");
        Assert.Contains("username", cols);
        Assert.DoesNotContain("windows_username", cols);

        Assert.Equal("nhan", c.ExecuteScalar<string>("SELECT username FROM Users WHERE id = 1;"));
        Assert.Equal("chi.le", c.ExecuteScalar<string>("SELECT username FROM Users WHERE id = 2;"));
        Assert.Null(c.ExecuteScalar<string?>("SELECT username FROM Users WHERE id = 3;")); // still nullable
    }

    [Fact]
    public async Task Upgrade_From_V9_Adds_RowVersion_Defaulting_To_1_On_Every_Versioned_Table()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        foreach (var table in VersionedTables)
        {
            Assert.Contains("row_version", Columns(c, table));
            // Every pre-existing row is backfilled to 1 by the column DEFAULT -- no NULLs, or the
            // check-and-bump template would never match.
            Assert.Equal(0, c.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM {table} WHERE row_version IS NOT 1;"));
        }
    }

    [Fact]
    public async Task RowVersion_Is_Deliberately_Absent_From_Unversioned_Tables()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        foreach (var table in UnversionedTables)
            Assert.DoesNotContain("row_version", Columns(c, table));
    }

    [Fact] // Never leave the system with nobody who can administer it.
    public async Task Upgrade_From_V9_Promotes_Exactly_One_Admin()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE is_admin = 1;"));
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT id FROM Users WHERE is_admin = 1;")); // the first user
    }

    [Fact]
    public async Task Upgrade_From_V9_Adds_Auth_And_Scope_Columns_With_Safe_Defaults()
    {
        SeedV9Database();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        var cols = Columns(c, "Users");
        Assert.Contains("password_hash", cols);
        Assert.Contains("is_admin", cols);
        Assert.Contains("active_team_id", cols);

        // Nobody has a password yet (auth arrives in M8.3) and nobody is scoped to a team.
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE password_hash IS NULL;"));
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE active_team_id = 0;"));
        Assert.Equal(2, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE is_admin = 0;"));
    }

    [Fact] // ADD COLUMN is not idempotent -- a relaunch must not re-run the v10 step.
    public async Task Upgrade_Is_Idempotent_Across_Relaunches()
    {
        SeedV9Database();
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(3, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE is_admin = 1;"));
    }

    // ---- Fresh-install path ----------------------------------------------------------------

    [Fact] // Guards the CreateTables invariant. Fails loudly if the v1 DDL is ever "tidied".
    public async Task FreshInstall_With_No_Database_File_Initialises_Straight_To_V10()
    {
        Assert.False(File.Exists(_dbPath)); // genuinely from zero

        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));

        var cols = Columns(c, "Users");
        Assert.Contains("username", cols);
        Assert.DoesNotContain("windows_username", cols); // the v10 rename really replayed
        Assert.Contains("password_hash", cols);
        Assert.Contains("is_admin", cols);
        Assert.Contains("active_team_id", cols);

        foreach (var table in VersionedTables)
            Assert.Contains("row_version", Columns(c, table));
    }

    [Fact] // A fresh install has no users at all, so there is nobody to promote -- and that is fine.
    public async Task FreshInstall_Has_No_Users_And_Therefore_No_Admin()
    {
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users;"));
        Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Users WHERE is_admin = 1;"));
    }

    [Fact] // Deleting the file and starting over must reach the same place, not a half-migrated one.
    public async Task FreshInstall_After_Deleting_The_Database_File_Reaches_V10_Again()
    {
        await _sut.InitializeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
        Assert.False(File.Exists(_dbPath));

        await _sut.InitializeAsync(); // rebuild from nothing

        using var c = _factory.Create();
        Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Contains("username", Columns(c, "Users"));
        Assert.Contains("row_version", Columns(c, "TimeLogs"));
    }

    [Fact] // New rows start at version 1, so the first check-and-bump on them matches.
    public async Task FreshInstall_New_Rows_Start_At_RowVersion_1()
    {
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        c.Execute("INSERT INTO Users(name, username, is_active) VALUES('Ada', 'ada', 1);");
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT row_version FROM Users WHERE username = 'ada';"));

        // The DEFAULT backlog the initializer seeds is a row nobody inserted explicitly -- check it too.
        Assert.Equal(1, c.ExecuteScalar<long>(
            "SELECT row_version FROM Backlogs WHERE backlog_code = 'DEFAULT';"));
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
