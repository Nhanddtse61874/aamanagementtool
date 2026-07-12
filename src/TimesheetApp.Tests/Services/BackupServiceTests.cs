using Moq;
using TimesheetApp.Config;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P9 (BK-01..06): user-controlled full-DB backup + restore to a chosen folder.
public class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsbk_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _backupFolder;

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db", "timesheet.db");
        _backupFolder = Path.Combine(_root, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    // Mutable fake config (mirrors what JsonAppConfig exposes) so SetX persists across calls in-test.
    private sealed class FakeConfig : IAppConfig
    {
        public string DbPath { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public string BackupFolderPath { get; set; } = "";
        public bool AutoBackupEnabled { get; set; }
        public int BackupKeepCount { get; set; } = 30;
        public int ActiveTeamId { get; set; }
        public string ExportRoot1Path { get; set; } = "";
        public string ExportRoot2Path { get; set; } = "";
        public bool RetentionEnabled { get; set; }
        public int RetentionMonths { get; set; } = 3;

        public void SetDbPath(string v) => DbPath = v;
        public void SetArchivePath(string v) => ArchivePath = v;
        public void SetBackupFolderPath(string v) => BackupFolderPath = v;
        public void SetAutoBackupEnabled(bool v) => AutoBackupEnabled = v;
        public void SetBackupKeepCount(int v) => BackupKeepCount = v;
        public void SetActiveTeamId(int v) => ActiveTeamId = v;
        public void SetExportRoot1Path(string v) => ExportRoot1Path = v;
        public void SetExportRoot2Path(string v) => ExportRoot2Path = v;
        public void SetRetentionEnabled(bool v) => RetentionEnabled = v;
        public void SetRetentionMonths(int v) => RetentionMonths = v;
        public bool IsDarkMode { get; set; }
        public void SetIsDarkMode(bool v) => IsDarkMode = v;
    }

    private (BackupService svc, FakeConfig cfg) Make(DateTimeOffset utcNow, bool setFolder = true, int keep = 30)
    {
        var cfg = new FakeConfig
        {
            DbPath = _dbPath,
            BackupFolderPath = setFolder ? _backupFolder : "",
            BackupKeepCount = keep,
        };
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(utcNow);
        return (new BackupService(cfg, clock.Object), cfg);
    }

    private static DateTimeOffset Local(int y, int mo, int d, int h, int mi, int s, int ms = 0) =>
        new(new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Local));

    [Fact]
    public async Task BackupNow_creates_timestamped_copy_and_returns_path()
    {
        TinyDb.Create(_dbPath, "LIVE-DB"); // a REAL database (was a text file: see TinyDb)
        var (svc, _) = Make(Local(2026, 6, 27, 9, 30, 15, 123));

        var path = await svc.BackupNowAsync();

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(new[] { "LIVE-DB" }, TinyDb.ReadAll(path!));
        Assert.Equal("ok", TinyDb.IntegrityCheck(path!));
        Assert.Equal("timesheet_20260627093015123.db", Path.GetFileName(path!)); // millisecond stamp
        Assert.Equal(_backupFolder, Path.GetDirectoryName(path!));
    }

    [Fact]
    public async Task BackupNow_returns_null_when_no_folder_set()
    {
        TinyDb.Create(_dbPath, "LIVE-DB"); // a REAL database (was a text file: see TinyDb)
        var (svc, _) = Make(Local(2026, 6, 27, 9, 0, 0), setFolder: false);

        Assert.Null(await svc.BackupNowAsync());
    }

    [Fact]
    public async Task BackupNow_returns_null_when_db_missing()
    {
        var (svc, _) = Make(Local(2026, 6, 27, 9, 0, 0)); // no db file written
        Assert.Null(await svc.BackupNowAsync());
    }

    [Fact]
    public async Task BackupNow_prunes_keeping_only_newest_N()
    {
        TinyDb.Create(_dbPath, "DATA");
        const int keep = 3;
        // 5 backups at strictly increasing seconds; only the newest 3 should survive.
        for (var i = 0; i < 5; i++)
        {
            var (svc, _) = Make(Local(2026, 6, 27, 10, 0, i), keep: keep);
            await svc.BackupNowAsync();
        }

        var files = Directory.GetFiles(_backupFolder, "timesheet_*.db");
        Assert.Equal(keep, files.Length);
        Assert.Contains(files, f => Path.GetFileName(f) == "timesheet_20260627100004000.db"); // newest kept
        Assert.DoesNotContain(files, f => Path.GetFileName(f) == "timesheet_20260627100000000.db"); // oldest pruned
    }

    [Fact]
    public async Task Prune_never_deletes_unrelated_files()
    {
        TinyDb.Create(_dbPath, "DATA");
        Directory.CreateDirectory(_backupFolder);
        var unrelated = Path.Combine(_backupFolder, "notes.txt");
        File.WriteAllText(unrelated, "keep me");

        // 4 app backups, keep=1.
        for (var i = 0; i < 4; i++)
            await Make(Local(2026, 6, 27, 11, 0, i), keep: 1).svc.BackupNowAsync();

        Assert.True(File.Exists(unrelated)); // untouched
        Assert.Single(Directory.GetFiles(_backupFolder, "timesheet_*.db"));
    }

    [Fact]
    public async Task ListBackups_parses_timestamps_and_orders_newest_first()
    {
        TinyDb.Create(_dbPath, "DATA");
        await Make(Local(2026, 6, 25, 8, 0, 0), keep: 30).svc.BackupNowAsync();
        await Make(Local(2026, 6, 27, 8, 0, 0), keep: 30).svc.BackupNowAsync();
        await Make(Local(2026, 6, 26, 8, 0, 0), keep: 30).svc.BackupNowAsync();

        var (svc, _) = Make(Local(2026, 6, 27, 12, 0, 0));
        var list = svc.ListBackups();

        Assert.Equal(3, list.Count);
        Assert.Equal(new DateTime(2026, 6, 27, 8, 0, 0), list[0].Timestamp);
        Assert.Equal(new DateTime(2026, 6, 26, 8, 0, 0), list[1].Timestamp);
        Assert.Equal(new DateTime(2026, 6, 25, 8, 0, 0), list[2].Timestamp);
        Assert.All(list, b => Assert.True(b.SizeBytes > 0));
    }

    [Fact]
    public void ListBackups_empty_when_no_folder()
    {
        var (svc, _) = Make(Local(2026, 6, 27, 9, 0, 0), setFolder: false);
        Assert.Empty(svc.ListBackups());
    }

    [Fact]
    public async Task AutoBackup_creates_one_when_due_and_skips_when_already_done_today()
    {
        TinyDb.Create(_dbPath, "DATA");
        var (svc, cfg) = Make(Local(2026, 6, 27, 9, 0, 0));
        cfg.AutoBackupEnabled = true;

        Assert.True(await svc.AutoBackupIfDueAsync());      // first run today -> backup made
        Assert.Single(svc.ListBackups());

        // Same day, later time -> already a backup dated today, so no second backup.
        var (svc2, cfg2) = Make(Local(2026, 6, 27, 17, 0, 0));
        cfg2.AutoBackupEnabled = true;
        Assert.False(await svc2.AutoBackupIfDueAsync());
        Assert.Single(svc2.ListBackups());
    }

    [Fact]
    public async Task AutoBackup_noop_when_disabled()
    {
        TinyDb.Create(_dbPath, "DATA");
        var (svc, cfg) = Make(Local(2026, 6, 27, 9, 0, 0));
        cfg.AutoBackupEnabled = false;

        Assert.False(await svc.AutoBackupIfDueAsync());
        Assert.Empty(svc.ListBackups());
    }

    [Fact]
    public async Task Restore_writes_pre_restore_safety_copy_then_swaps_db_contents()
    {
        TinyDb.Create(_dbPath, "CURRENT-DB");
        Directory.CreateDirectory(_backupFolder);
        var backupPath = Path.Combine(_backupFolder, "timesheet_20260620080000.db");
        TinyDb.Create(backupPath, "OLD-BACKUP-DB");

        var (svc, _) = Make(Local(2026, 6, 27, 13, 0, 0));
        await svc.RestoreAsync(backupPath);

        // live db now holds the backup's rows...
        Assert.Equal(new[] { "OLD-BACKUP-DB" }, TinyDb.ReadAll(_dbPath));
        // ...and the pre-restore safety copy preserves the previous live db — as a restorable database,
        // not just as bytes: it is the file the user reaches for when the restore was the wrong one.
        var safety = Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "*.pre-restore_*.bak");
        Assert.Single(safety);
        Assert.Equal(new[] { "CURRENT-DB" }, TinyDb.ReadAll(safety[0]));
        Assert.Equal("ok", TinyDb.IntegrityCheck(safety[0]));
    }

    [Fact]
    public async Task Restore_throws_when_backup_unreadable()
    {
        TinyDb.Create(_dbPath, "CURRENT-DB");
        var (svc, _) = Make(Local(2026, 6, 27, 13, 0, 0));
        var missing = Path.Combine(_backupFolder, "timesheet_20990101000000.db");

        await Assert.ThrowsAsync<FileNotFoundException>(() => svc.RestoreAsync(missing));
    }

    [Fact] // Guard: restoring the live DB onto itself is rejected and leaves the live DB untouched.
    public async Task Restore_rejects_restoring_live_db_onto_itself()
    {
        TinyDb.Create(_dbPath, "CURRENT-DB");
        var (svc, _) = Make(Local(2026, 6, 27, 13, 0, 0));

        // Pass the same path with different casing to exercise the case-insensitive compare.
        var samePath = _dbPath.ToUpperInvariant();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(samePath));

        Assert.Equal(new[] { "CURRENT-DB" }, TinyDb.ReadAll(_dbPath)); // untouched
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, "*.pre-restore_*.bak")); // no safety copy made
    }

    // P11 (EX-05): BackupToFolderAsync copies the live .db into an arbitrary folder + prunes to keep.
    [Fact]
    public async Task BackupToFolder_copies_db_into_given_folder_and_prunes()
    {
        TinyDb.Create(_dbPath, "LIVE-DB"); // a REAL database (was a text file: see TinyDb)
        var target = Path.Combine(_root, "exportroot", "db");

        // 4 copies into the target with keep=2; only the newest 2 survive.
        for (var i = 0; i < 4; i++)
        {
            var (svc, _) = Make(Local(2026, 6, 27, 14, 0, i));
            var path = await svc.BackupToFolderAsync(target, keep: 2);
            Assert.NotNull(path);
            Assert.Equal(target, Path.GetDirectoryName(path!));
        }

        var files = Directory.GetFiles(target, "timesheet_*.db");
        Assert.Equal(2, files.Length);
        Assert.Equal(new[] { "LIVE-DB" }, TinyDb.ReadAll(files[0]));
    }

    [Fact]
    public async Task BackupToFolder_returns_null_when_db_missing()
    {
        var (svc, _) = Make(Local(2026, 6, 27, 9, 0, 0)); // no db file written
        Assert.Null(await svc.BackupToFolderAsync(Path.Combine(_root, "x", "db"), keep: 5));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp file briefly held on Windows — leave it for the OS to reap */ }
    }
}
