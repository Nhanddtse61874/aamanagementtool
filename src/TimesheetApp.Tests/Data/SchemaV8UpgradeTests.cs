using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// R3 (Critical): the v8 migration must be additive, data-preserving, and idempotent — a bad migration
// bricks startup for every OneDrive user. This stands up a DB pinned at schema v7 (all v7 columns/tables,
// no team_id columns, no Teams/UserTeams), runs the REAL initializer, and proves the v7->v8 upgrade.
public class SchemaV8UpgradeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SchemaV8UpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-v8up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
    }

    // Builds a DB in the v7 end-state: Backlogs (with all v7 columns) + StandupEntries, but NONE of the
    // v8 team_id columns and no Teams/UserTeams tables, PRAGMA user_version = 7.
    private void SeedV7Database()
    {
        using var c = _factory.Create();
        c.Execute(@"
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
    pca_contact_id          INTEGER
);
CREATE TABLE BacklogAudit (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_id         INTEGER NOT NULL,
    field              TEXT    NOT NULL,
    old_value          TEXT,
    new_value          TEXT,
    changed_by_user_id INTEGER,
    changed_by_name    TEXT,
    changed_at         TEXT    NOT NULL
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
    created_at   TEXT    NOT NULL
);
INSERT INTO Backlogs(backlog_code, project, created_at) VALUES('REQ-OLD', 'ARCS', '2026-06-01T00:00:00Z');
INSERT INTO StandupEntries(user_id, work_date, section, backlog_code, task_text, status, created_at)
    VALUES(1, '2026-06-20', 'today', 'REQ-OLD', 'did work', 'Done', '2026-06-20T00:00:00Z');
PRAGMA user_version = 7;");
    }

    private static IReadOnlyList<string> Columns(IDbConnection c, string table) =>
        c.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    private static bool TableExists(IDbConnection c, string table) =>
        c.ExecuteScalar<string?>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table }) is not null;

    [Fact]
    public async Task V7_to_v8_upgrade_adds_team_columns_and_tables_preserves_data_and_is_idempotent()
    {
        SeedV7Database();

        using (var pre = _factory.Create())
        {
            Assert.Equal(7, pre.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.DoesNotContain("team_id", Columns(pre, "Backlogs"));
            Assert.DoesNotContain("team_id", Columns(pre, "StandupEntries"));
            Assert.False(TableExists(pre, "Teams"));
            Assert.False(TableExists(pre, "UserTeams"));
        }

        // First run: v7 -> v8.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            // A v7 DB now upgrades all the way to the latest schema (v9) — the v8 step still runs
            // (adds the team columns/tables below) and the v9 step runs after it.
            Assert.Equal(9, c.ExecuteScalar<long>("PRAGMA user_version;"));

            // team_id columns added (nullable) to both tables.
            Assert.Contains("team_id", Columns(c, "Backlogs"));
            Assert.Contains("team_id", Columns(c, "StandupEntries"));

            // Teams / UserTeams tables created.
            Assert.True(TableExists(c, "Teams"), "Teams should exist after upgrade");
            Assert.True(TableExists(c, "UserTeams"), "UserTeams should exist after upgrade");

            // Existing rows intact; their new team_id is NULL (backfill is a later wave's bootstrap).
            var backlog = c.QuerySingle<(string code, string project, long? team)>(
                "SELECT backlog_code AS code, project, team_id AS team FROM Backlogs WHERE backlog_code='REQ-OLD';");
            Assert.Equal("ARCS", backlog.project);
            Assert.Null(backlog.team);

            var entryTeam = c.ExecuteScalar<long?>(
                "SELECT team_id FROM StandupEntries WHERE backlog_code='REQ-OLD';");
            Assert.Null(entryTeam);

            // No teams seeded by the initializer (bootstrap owns that).
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Teams;"));
        }

        // Idempotent re-run must not throw (no duplicate ADD COLUMN) and must not change state.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            Assert.Equal(9, c.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Backlogs WHERE backlog_code='REQ-OLD';"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM StandupEntries WHERE backlog_code='REQ-OLD';"));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
