using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Config;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>M10 Blocker 2 / P2: the offline restore CLI. Every path here pins its own database and its own
/// arbitrary probe PORT into a fresh temp directory that does not exist until the test creates it — never
/// the real database, never the real port 5080 (the M10 brief is explicit: nothing may listen on 5080
/// during `dotnet test`). <see cref="RestoreCli.RunAsync"/> takes the probe port as a parameter for exactly
/// this reason: production always calls it with <see cref="RestoreCli.DefaultProbePort"/> (5080), but these
/// tests exercise the SAME logic against a throwaway port they bind themselves.</summary>
public sealed class RestoreCliTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "restorecli_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _backupPath;

    public RestoreCliTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db", "timesheet.db");
        _backupPath = Path.Combine(_root, "backup", "timesheet_20260620080000000.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
    }

    // Bare-bones IAppConfig: RestoreCli only ever reads DbPath.
    private sealed class FakeConfig : IAppConfig
    {
        public string DbPath { get; set; } = "";
        public void SetDbPath(string dbPath) => DbPath = dbPath;
        public string ArchivePath { get; set; } = "";
        public void SetArchivePath(string v) => ArchivePath = v;
        public string BackupFolderPath { get; set; } = "";
        public void SetBackupFolderPath(string v) => BackupFolderPath = v;
        public bool AutoBackupEnabled { get; set; }
        public void SetAutoBackupEnabled(bool v) => AutoBackupEnabled = v;
        public int BackupKeepCount { get; set; } = 30;
        public void SetBackupKeepCount(int v) => BackupKeepCount = v;
        public string ExportRoot1Path { get; set; } = "";
        public void SetExportRoot1Path(string v) => ExportRoot1Path = v;
        public string ExportRoot2Path { get; set; } = "";
        public void SetExportRoot2Path(string v) => ExportRoot2Path = v;
        public bool RetentionEnabled { get; set; }
        public void SetRetentionEnabled(bool v) => RetentionEnabled = v;
        public int RetentionMonths { get; set; } = 3;
        public void SetRetentionMonths(int v) => RetentionMonths = v;
    }

    // A minimal REAL SQLite database (not a text file): CREATE TABLE + one row is enough for
    // PRAGMA integrity_check to answer "ok", which is what SqliteOnlineBackup.IsIntact requires.
    private static void CreateRealDb(string path, string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Marker(value TEXT); INSERT INTO Marker(value) VALUES ($v);";
            cmd.Parameters.AddWithValue("$v", marker);
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static string ReadMarker(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM Marker LIMIT 1;";
        var result = (string)cmd.ExecuteScalar()!;
        SqliteConnection.ClearAllPools();
        return result;
    }

    private static int BindFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public void IsRestoreCommand_true_only_for_leading_restore_flag()
    {
        Assert.True(RestoreCli.IsRestoreCommand(new[] { "--restore", "x.db" }));
        Assert.True(RestoreCli.IsRestoreCommand(new[] { "--RESTORE", "x.db" })); // case-insensitive
        Assert.False(RestoreCli.IsRestoreCommand(Array.Empty<string>()));
        Assert.False(RestoreCli.IsRestoreCommand(new[] { "--list-backups" }));
    }

    [Fact]
    public async Task Missing_path_argument_prints_usage_and_exits_nonzero()
    {
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();

        var exitCode = await RestoreCli.RunAsync(new[] { "--restore" }, config, output);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public async Task Refuses_when_live_db_does_not_exist_and_does_not_create_one()
    {
        CreateRealDb(_backupPath, "BACKUP-CONTENT");
        // dbPath deliberately never created -> simulates HOLE 11 (wrong resolved path).
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();

        var exitCode = await RestoreCli.RunAsync(new[] { "--restore", _backupPath }, config, output);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("no database found", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_dbPath)); // must NOT have been created as a side effect
    }

    [Fact]
    public async Task Refuses_when_probe_port_is_occupied()
    {
        CreateRealDb(_dbPath, "LIVE-CONTENT");
        CreateRealDb(_backupPath, "BACKUP-CONTENT");
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();

        // Hold a throwaway port open ourselves to simulate "the API is still running" -- never port 5080.
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var exitCode = await RestoreCli.RunAsync(
            new[] { "--restore", _backupPath }, config, output, probePort: occupiedPort);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("already listening", output.ToString(), StringComparison.OrdinalIgnoreCase);
        // Refused before touching the live db at all.
        Assert.Equal("LIVE-CONTENT", ReadMarker(_dbPath));
    }

    [Fact]
    public async Task Successful_restore_swaps_content_verifies_integrity_and_exits_zero()
    {
        CreateRealDb(_dbPath, "LIVE-CONTENT");
        CreateRealDb(_backupPath, "BACKUP-CONTENT");
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();
        var freePort = BindFreeLoopbackPort(); // bound-and-released -> free at call time

        var exitCode = await RestoreCli.RunAsync(
            new[] { "--restore", _backupPath }, config, output, probePort: freePort);

        Assert.Equal(0, exitCode);
        Assert.Equal("BACKUP-CONTENT", ReadMarker(_dbPath));
        Assert.Contains("Restore complete and verified", output.ToString());

        // BackupService.RestoreAsync's own safety copy of the PREVIOUS live db must exist too.
        var safetyCopies = Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "*.pre-restore_*.bak");
        Assert.Single(safetyCopies);
    }

    [Fact]
    public async Task Unreadable_backup_file_fails_loudly_and_leaves_live_db_untouched()
    {
        CreateRealDb(_dbPath, "LIVE-CONTENT");
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();
        var freePort = BindFreeLoopbackPort();
        var missingBackup = Path.Combine(_root, "backup", "does-not-exist.db");

        var exitCode = await RestoreCli.RunAsync(
            new[] { "--restore", missingBackup }, config, output, probePort: freePort);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("restore failed", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("LIVE-CONTENT", ReadMarker(_dbPath)); // untouched
    }

    [Fact]
    public async Task Backup_that_is_not_a_real_database_is_rejected_before_anything_destructive()
    {
        CreateRealDb(_dbPath, "LIVE-CONTENT");
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllBytes(_backupPath, new byte[] { 9, 9, 9, 9, 9, 9 }); // exists, non-empty, not a db
        var config = new FakeConfig { DbPath = _dbPath };
        var output = new StringWriter();
        var freePort = BindFreeLoopbackPort();

        var exitCode = await RestoreCli.RunAsync(
            new[] { "--restore", _backupPath }, config, output, probePort: freePort);

        Assert.NotEqual(0, exitCode);
        Assert.Equal("LIVE-CONTENT", ReadMarker(_dbPath)); // untouched -- IsIntact(backupPath) caught it first
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "*.pre-restore_*.bak"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp file briefly held on Windows -- leave it for the OS to reap */ }
    }
}
