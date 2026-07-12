using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;
using Xunit;

namespace TimesheetApp.Tests.Data;

// M8.2 (design §6.3): the Server profile is the API host's opposite-of-desktop connection
// policy (WAL, pooled, bounded-wait). Kept in a separate file/class from
// SqliteConnectionFactoryTests, which hard-asserts the Desktop DEFAULT and must keep passing
// completely unchanged -- that is the evidence adding this profile did not touch the default.
public class SqliteConnectionFactoryServerProfileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SqliteConnectionFactoryServerProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-cf-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg, SqliteProfile.Server);
    }

    [Fact]
    public void Server_Profile_Sets_Journal_Mode_To_Wal()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)cmd.ExecuteScalar()!;
        Assert.Equal("wal", mode.ToLowerInvariant());
    }

    [Fact]
    public void Server_Profile_Creates_Wal_And_Shm_Sidecars_On_Write()
    {
        // Assert while the connection is still open. With Pooling=true, Dispose() returns the
        // handle to the ADO.NET pool instead of truly closing it, and draining the pool
        // (SqliteConnection.ClearAllPools, in Dispose() below) triggers an auto-checkpoint that
        // removes these sidecars again -- verified empirically against Microsoft.Data.Sqlite
        // 8.0.10. Right after the write is the only deterministic point to observe them.
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS Probe(id INTEGER PRIMARY KEY); " +
                          "INSERT INTO Probe DEFAULT VALUES;";
        cmd.ExecuteNonQuery();

        Assert.True(File.Exists(_dbPath + "-wal"), "-wal sidecar must exist under WAL (server profile)");
        Assert.True(File.Exists(_dbPath + "-shm"), "-shm sidecar must exist under WAL (server profile)");
    }

    [Fact]
    public void Server_Profile_Keeps_Foreign_Keys_On()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1L, result); // DATA-06 holds on both profiles
    }

    [Fact]
    public void Server_Profile_Sets_Busy_Timeout_And_Synchronous_Normal()
    {
        using var conn = _factory.Create();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout;";
            Assert.Equal(1000L, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA synchronous;";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar())); // 1 = NORMAL
        }
    }

    [Fact]
    public void Server_Profile_Lowers_Default_Command_Timeout_To_Five_Seconds()
    {
        // §8.4: busy_timeout alone does not bound a blocked writer -- Microsoft.Data.Sqlite
        // auto-retries SQLITE_BUSY up to CommandTimeout (default 30s; measured 33,940ms with
        // rev. 1's settings). "Default Timeout" on the connection string is what actually
        // lowers that ceiling, and it flows into every command/Dapper call made through this
        // connection without touching a repository.
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        Assert.Equal(5, cmd.CommandTimeout);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
