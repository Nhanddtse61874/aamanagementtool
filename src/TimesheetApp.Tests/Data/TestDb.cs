using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

// Shared temp-file SQLite fixture for repository integration tests (architecture spec §9).
// Builds the schema with the REAL DatabaseInitializer, then exposes itself as an
// IConnectionFactory pointed at that temp file. Reused by T4 (TimeLogRepository) and T5
// (User/Task/Backlog/Settings) repo tests. Seed helpers use raw SQL via Create() so the
// fixture has no dependency on any repository implementation.
public sealed class TestDb : IConnectionFactory, IDisposable
{
    private readonly IConnectionFactory _inner;
    private readonly string _dir;
    private readonly string _dbPath;

    public string Path => _dbPath;

    private TestDb(IConnectionFactory inner, string dir, string dbPath)
    {
        _inner = inner;
        _dir = dir;
        _dbPath = dbPath;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tsdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = System.IO.Path.Combine(dir, "timesheet.db");

        var cfg = new JsonAppConfig(System.IO.Path.Combine(dir, "appsettings.json"), dbPath);
        var inner = new SqliteConnectionFactory(cfg);
        var db = new TestDb(inner, dir, dbPath);

        // Real P1 initializer: schema + DEFAULT request + seed DefaultTasks.
        await new DatabaseInitializer(inner).InitializeAsync();
        return db;
    }

    public IDbConnection Create() => _inner.Create();

    // ---- Seed helpers (raw SQL; reusable by T4 + T5 tests) -------------------------------

    // Inserts an active user; returns its new id.
    public async Task<int> SeedUserAsync(string name = "Tester", string? windowsUsername = null, bool isActive = true)
    {
        using var c = Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, windows_username, is_active)
              VALUES(@name, @win, @active);
              SELECT last_insert_rowid();",
            new { name, win = windowsUsername, active = isActive ? 1 : 0 });
    }

    // Inserts a request; returns its new id.
    public async Task<int> SeedRequestAsync(string code = "REQ-001", string project = "ProjectX")
    {
        using var c = Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Backlogs(backlog_code, project, created_at)
              VALUES(@code, @project, @now);
              SELECT last_insert_rowid();",
            new { code, project, now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });
    }

    // Inserts a task under a request; returns its new id.
    public async Task<int> SeedTaskAsync(int requestId, string taskName = "Implement", int orderIndex = 0, bool isActive = true)
    {
        using var c = Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tasks(backlog_id, task_name, order_index, is_active)
              VALUES(@requestId, @taskName, @order, @active);
              SELECT last_insert_rowid();",
            new { requestId, taskName, order = orderIndex, active = isActive ? 1 : 0 });
    }

    // Convenience: seeds one user + one request + one task; returns (userId, taskId).
    public async Task<(int UserId, int TaskId)> SeedUserAndTaskAsync()
    {
        var userId = await SeedUserAsync();
        var requestId = await SeedRequestAsync();
        var taskId = await SeedTaskAsync(requestId);
        return (userId, taskId);
    }

    // Soft-deletes a task (is_active=0) — used to prove report joins still resolve names (XC-06).
    public async Task SetTaskActiveAsync(int taskId, bool isActive)
    {
        using var c = Create();
        await c.ExecuteAsync(
            "UPDATE Tasks SET is_active = @active WHERE id = @id;",
            new { id = taskId, active = isActive ? 1 : 0 });
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
