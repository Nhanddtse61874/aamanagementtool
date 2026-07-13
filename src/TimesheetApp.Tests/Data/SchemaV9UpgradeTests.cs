using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// FIX-C13 (Critical): the v9 migration must be additive, data-preserving, and idempotent — a bad migration
// bricks startup for every OneDrive user. This stands up a DB pinned at schema v8 (all v8 columns/tables,
// no Tasks.type/assignee_user_id, no BacklogAudit.note, no TaskTags/TaskAudit), runs the REAL initializer,
// and proves the v8->v9 upgrade.
public class SchemaV9UpgradeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SchemaV9UpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-v9up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
    }

    // Builds a DB in the v8 end-state: Backlogs/BacklogAudit/Tasks (post-v6 rename, with the v7 Backlog
    // columns and v8 team_id), StandupEntries + Teams + UserTeams, but NONE of the v9 columns/tables
    // (Tasks.type, Tasks.assignee_user_id, BacklogAudit.note, TaskTags, TaskAudit), PRAGMA user_version = 8.
    private void SeedV8Database()
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
    changed_at         TEXT    NOT NULL
);
CREATE TABLE Tasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_id  INTEGER NOT NULL,
    task_name   TEXT    NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1,
    status      TEXT    NOT NULL DEFAULT 'Todo'
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
    team_id      INTEGER
);
CREATE TABLE Teams (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,
    is_active  INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL
);
CREATE TABLE UserTeams (
    user_id INTEGER NOT NULL,
    team_id INTEGER NOT NULL,
    PRIMARY KEY (user_id, team_id)
);
INSERT INTO Backlogs(backlog_code, project, created_at, period_month, type)
    VALUES('REQ-OLD', 'ARCS', '2026-06-01T00:00:00Z', '2026-06', 'Implement');
INSERT INTO Tasks(backlog_id, task_name, order_index, is_active, status)
    VALUES(1, 'old task', 0, 1, 'Doing');
PRAGMA user_version = 8;");
    }

    private static IReadOnlyList<string> Columns(IDbConnection c, string table) =>
        c.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    private static bool TableExists(IDbConnection c, string table) =>
        c.ExecuteScalar<string?>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table }) is not null;

    [Fact]
    public async Task V8_to_v9_upgrade_adds_columns_and_tables_preserves_data_and_is_idempotent()
    {
        SeedV8Database();

        using (var pre = _factory.Create())
        {
            Assert.Equal(8, pre.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.DoesNotContain("type", Columns(pre, "Tasks"));
            Assert.DoesNotContain("assignee_user_id", Columns(pre, "Tasks"));
            Assert.DoesNotContain("note", Columns(pre, "BacklogAudit"));
            Assert.False(TableExists(pre, "TaskTags"));
            Assert.False(TableExists(pre, "TaskAudit"));
        }

        // First run: v8 -> v9.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            // A v8 DB upgrades all the way to the latest schema (v10): the v9 step runs, then v10.
            Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));

            // Tasks gains type + assignee_user_id (nullable).
            var taskCols = Columns(c, "Tasks");
            Assert.Contains("type", taskCols);
            Assert.Contains("assignee_user_id", taskCols);

            // BacklogAudit gains note (nullable).
            Assert.Contains("note", Columns(c, "BacklogAudit"));

            // TaskTags / TaskAudit tables created.
            Assert.True(TableExists(c, "TaskTags"), "TaskTags should exist after upgrade");
            Assert.True(TableExists(c, "TaskAudit"), "TaskAudit should exist after upgrade");

            // Existing backlog row intact; unaffected by the upgrade.
            var backlog = c.QuerySingle<(string code, string project, string? type)>(
                "SELECT backlog_code AS code, project, type FROM Backlogs WHERE backlog_code='REQ-OLD';");
            Assert.Equal("ARCS", backlog.project);
            Assert.Equal("Implement", backlog.type);

            // Existing task row intact; the new columns are NULL on it (not mutated by the upgrade).
            var task = c.QuerySingle<(string name, string status, string? type, long? assignee)>(
                "SELECT task_name AS name, status, type, assignee_user_id AS assignee FROM Tasks WHERE backlog_id=1;");
            Assert.Equal("old task", task.name);
            Assert.Equal("Doing", task.status);
            Assert.Null(task.type);
            Assert.Null(task.assignee);
        }

        // Idempotent re-run must not throw (no duplicate ADD COLUMN) and must not change state.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            Assert.Equal(10, c.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Backlogs WHERE backlog_code='REQ-OLD';"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Tasks WHERE backlog_id=1;"));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
