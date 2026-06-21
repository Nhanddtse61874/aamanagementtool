using Xunit;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;

namespace TimesheetApp.Tests.Data;

public class DatabaseInitializerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;
    private readonly DatabaseInitializer _sut;

    private static readonly string[] ExpectedTables =
    {
        "Users", "Requests", "Tasks", "TaskTemplates", "TimeLogs", "DefaultTasks", "Settings"
    };

    public DatabaseInitializerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
        _sut = new DatabaseInitializer(_factory);
    }

    private long Count(IDbConnection c, string sql) => c.ExecuteScalar<long>(sql);

    [Fact]
    public async Task InitializeAsync_Creates_All_Seven_Tables()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        foreach (var table in ExpectedTables)
        {
            var found = c.ExecuteScalar<string?>(
                "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;", new { t = table });
            Assert.Equal(table, found);
        }
    }

    [Fact]
    public async Task InitializeAsync_Is_Idempotent_No_Duplicate_Default_Request()
    {
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();
        await _sut.InitializeAsync();

        using var c = _factory.Create();
        var defaults = Count(c, "SELECT COUNT(*) FROM Requests WHERE request_code='DEFAULT';");
        Assert.Equal(1, defaults); // DATA-03 idempotent
    }

    [Fact]
    public async Task InitializeAsync_Seeds_DefaultTasks_Only_When_Empty()
    {
        await _sut.InitializeAsync();
        using (var c = _factory.Create())
        {
            // user renames a default -> simulate a curated table
            c.Execute("DELETE FROM DefaultTasks;");
            c.Execute("INSERT INTO DefaultTasks(task_name, order_index, is_active) VALUES('Custom Only', 0, 1);");
        }

        await _sut.InitializeAsync(); // relaunch must NOT re-seed over the curated row

        using (var c = _factory.Create())
        {
            var count = Count(c, "SELECT COUNT(*) FROM DefaultTasks;");
            Assert.Equal(1, count); // DATA-04: not re-seeded
            var name = c.ExecuteScalar<string>("SELECT task_name FROM DefaultTasks;");
            Assert.Equal("Custom Only", name);
        }
    }

    [Fact]
    public async Task InitializeAsync_Sets_User_Version_To_Target()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        var version = Count(c, "PRAGMA user_version;");
        Assert.True(version >= 1, "user_version must advance to the schema target (DATA-05)");
    }

    [Fact]
    public async Task Schema_Has_Unique_Natural_Key_On_TimeLogs()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        // seed FK targets
        c.Execute("INSERT INTO Users(name, is_active) VALUES('U', 1);");
        c.Execute("INSERT INTO Requests(request_code, project, created_at) VALUES('R1','P','2026-06-21T00:00:00Z');");
        c.Execute("INSERT INTO Tasks(request_id, task_name, order_index, is_active) VALUES(2,'T',0,1);");
        c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                  "VALUES(1,1,'2026-06-22',8.0,'2026-06-21T00:00:00Z');");

        var ex = Assert.ThrowsAny<SqliteException>(() =>
            c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                      "VALUES(1,1,'2026-06-22',4.0,'2026-06-21T00:00:00Z');"));
        Assert.Contains("UNIQUE", ex.Message, StringComparison.OrdinalIgnoreCase); // DATA-02
    }

    [Fact]
    public async Task Foreign_Keys_Reject_TimeLog_With_Missing_Task()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        c.Execute("INSERT INTO Users(name, is_active) VALUES('U', 1);");

        var ex = Assert.ThrowsAny<SqliteException>(() =>
            c.Execute("INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at) " +
                      "VALUES(1, 9999, '2026-06-22', 8.0, '2026-06-21T00:00:00Z');"));
        Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase); // DATA-06
    }

    [Fact]
    public async Task Requests_Table_Has_No_IsActive_Column()
    {
        await _sut.InitializeAsync();
        using var c = _factory.Create();
        var cols = c.Query<string>("SELECT name FROM pragma_table_info('Requests');").ToList();
        Assert.DoesNotContain("is_active", cols); // DATA-02 decision 4
        Assert.Contains("windows_username",
            c.Query<string>("SELECT name FROM pragma_table_info('Users');").ToList()); // DATA-02
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
