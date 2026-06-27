using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// R1 (Critical): a bad v7 migration bricks startup for every OneDrive user. This test stands up a
// DB pinned at schema v6 (Backlogs already renamed, no v7 columns/tables), runs the REAL initializer,
// and proves the v6->v7 upgrade is additive, data-preserving, and idempotent.
public class SchemaV7UpgradeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SchemaV7UpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-v7up-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
    }

    // Builds a DB in the v6 end-state: Backlogs/BacklogAudit (post-rename), Tasks with status, the v2-v4
    // Backlog columns, but NONE of the v7 columns/tables, and PRAGMA user_version = 6.
    private void SeedV6Database()
    {
        using var c = _factory.Create();
        c.Execute(@"
CREATE TABLE Backlogs (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    backlog_code     TEXT    NOT NULL,
    project          TEXT    NOT NULL,
    created_at       TEXT    NOT NULL,
    start_date       TEXT,
    end_date         TEXT,
    period_month     TEXT,
    type             TEXT,
    assignee_user_id INTEGER
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
INSERT INTO Backlogs(backlog_code, project, created_at, period_month, type)
    VALUES('REQ-OLD', 'ARCS', '2026-06-01T00:00:00Z', '2026-06', 'Implement');
PRAGMA user_version = 6;");
    }

    private static IReadOnlyList<string> Columns(IDbConnection c, string table) =>
        c.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToList();

    private static bool TableExists(IDbConnection c, string table) =>
        c.ExecuteScalar<string?>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table }) is not null;

    [Fact]
    public async Task V6_to_v7_upgrade_adds_columns_and_tables_preserves_data_and_is_idempotent()
    {
        SeedV6Database();

        using (var pre = _factory.Create())
        {
            Assert.Equal(6, pre.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.DoesNotContain("deadline_internal", Columns(pre, "Backlogs"));
            Assert.False(TableExists(pre, "Tags"));
        }

        // First run: v6 -> v7.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            // A v6 DB now upgrades all the way to the latest schema (v8) — the v7 step still runs
            // (adds the columns/tables below) and the v8 step runs after it.
            Assert.Equal(8, c.ExecuteScalar<long>("PRAGMA user_version;"));

            var cols = Columns(c, "Backlogs");
            foreach (var col in new[]
            {
                "deadline_internal", "deadline_external", "rough_estimate_hours",
                "official_estimate_hours", "progress_percent", "note", "pca_contact_id",
            })
            {
                Assert.Contains(col, cols);
            }

            foreach (var table in new[] { "Tags", "BacklogTags", "PcaContacts", "Holidays" })
            {
                Assert.True(TableExists(c, table), $"{table} should exist after upgrade");
            }

            // Existing row intact; the new columns are NULL on it.
            var row = c.QuerySingle<(string code, string project, string? deadline)>(
                "SELECT backlog_code AS code, project, deadline_internal AS deadline FROM Backlogs WHERE backlog_code='REQ-OLD';");
            Assert.Equal("ARCS", row.project);
            Assert.Null(row.deadline);
        }

        // Idempotent re-run must not throw (no duplicate ADD COLUMN) and must not change state.
        await new DatabaseInitializer(_factory).InitializeAsync();

        using (var c = _factory.Create())
        {
            Assert.Equal(8, c.ExecuteScalar<long>("PRAGMA user_version;"));
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM Backlogs WHERE backlog_code='REQ-OLD';"));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
