using System.Data;
using Microsoft.Data.Sqlite;
using TimesheetApp.Config;
using TimesheetApp.Data;
using Xunit;

namespace TimesheetApp.Tests.Data;

public class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly IConnectionFactory _factory;

    public SqliteConnectionFactoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tsapp-cf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), _dbPath);
        _factory = new SqliteConnectionFactory(cfg);
    }

    [Fact]
    public void Create_Returns_Open_Connection()
    {
        using var conn = _factory.Create();
        Assert.Equal(ConnectionState.Open, conn.State);
    }

    [Fact]
    public void Foreign_Keys_Are_On_For_Every_Connection()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var result = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1L, result); // DATA-06
    }

    [Fact]
    public void Journal_Mode_Is_Delete_Not_Wal()
    {
        using var conn = _factory.Create();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = (string)cmd.ExecuteScalar()!;
        Assert.Equal("delete", mode.ToLowerInvariant()); // XC-01
    }

    [Fact]
    public void No_Wal_Or_Shm_Sidecar_Is_Created_By_A_Write()
    {
        // Do real work through a short connection, then dispose it.
        using (var conn = _factory.Create())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Probe(id INTEGER PRIMARY KEY); " +
                              "INSERT INTO Probe DEFAULT VALUES;";
            cmd.ExecuteNonQuery();
        }

        Assert.False(File.Exists(_dbPath + "-wal"), "-wal sidecar must never exist (XC-01)");
        Assert.False(File.Exists(_dbPath + "-shm"), "-shm sidecar must never exist (XC-01)");
        Assert.True(File.Exists(_dbPath), "the single .db file is the only at-rest artifact");
    }

    [Fact]
    public void Pooling_Is_Off_So_Dispose_Releases_The_File_Handle()
    {
        using (var conn = _factory.Create())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Probe(id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        // With Pooling=False the handle is released on Dispose; the file is movable/deletable.
        File.Move(_dbPath, _dbPath + ".moved");
        Assert.True(File.Exists(_dbPath + ".moved"));
    }

    [Fact]
    public void Create_AutoCreates_Missing_Parent_Directory()
    {
        // First-run regression: DB path under a directory that does NOT exist yet
        // (e.g. %APPDATA%\TimesheetApp on a fresh machine). SQLite cannot create the
        // file without its parent dir -> "unable to open database file" (SQLite Error 14).
        var nestedDir = Path.Combine(_dir, "does", "not", "exist", "yet");
        Assert.False(Directory.Exists(nestedDir));
        var cfg = new JsonAppConfig(Path.Combine(_dir, "appsettings2.json"), Path.Combine(nestedDir, "timesheet.db"));
        var factory = new SqliteConnectionFactory(cfg);

        using var conn = factory.Create();

        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.True(Directory.Exists(nestedDir));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
