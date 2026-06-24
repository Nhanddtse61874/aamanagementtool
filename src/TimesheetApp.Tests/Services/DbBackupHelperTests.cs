using Moq;
using TimesheetApp.Config;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class DbBackupHelperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tsbackup_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public DbBackupHelperTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "timesheet.db");
    }

    private DbBackupHelper Make(DateTimeOffset now)
    {
        var cfg = new Mock<IAppConfig>();
        cfg.SetupGet(c => c.DbPath).Returns(_dbPath);
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);
        return new DbBackupHelper(cfg.Object, clock.Object);
    }

    [Fact]
    public async Task BackupAsync_copies_db_to_timestamped_file()
    {
        File.WriteAllText(_dbPath, "SQLITE-DATA");
        var helper = Make(new DateTimeOffset(2026, 6, 21, 9, 30, 15, 123, TimeSpan.Zero));

        var backupPath = await helper.BackupAsync();

        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath));
        Assert.Equal("SQLITE-DATA", File.ReadAllText(backupPath!));
        Assert.Contains("20260621093015123", Path.GetFileName(backupPath!));
        Assert.EndsWith(".bak", backupPath!);
    }

    [Fact] // XC-10: the DB folder must not grow unbounded — only the newest N .bak files are kept.
    public async Task BackupAsync_prunes_old_backups_keeping_only_the_newest_N()
    {
        File.WriteAllText(_dbPath, "DATA");
        var baseTime = new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero);
        var total = DbBackupHelper.KeepBackups + 5;
        for (var i = 0; i < total; i++)
            await Make(baseTime.AddSeconds(i)).BackupAsync(); // strictly increasing timestamps

        var remaining = Directory.GetFiles(_dir, "timesheet.db.*.bak");
        Assert.Equal(DbBackupHelper.KeepBackups, remaining.Length);

        // Newest stamp kept, oldest pruned.
        var newest = baseTime.AddSeconds(total - 1).ToString("yyyyMMddHHmmssfff");
        var oldest = baseTime.ToString("yyyyMMddHHmmssfff");
        Assert.Contains(remaining, f => Path.GetFileName(f).Contains(newest));
        Assert.DoesNotContain(remaining, f => Path.GetFileName(f).Contains(oldest));
    }

    [Fact]
    public async Task BackupAsync_returns_null_when_db_missing()
    {
        var helper = Make(DateTimeOffset.UtcNow);
        var backupPath = await helper.BackupAsync();
        Assert.Null(backupPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
