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
