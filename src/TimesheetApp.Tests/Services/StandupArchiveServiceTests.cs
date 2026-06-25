using System.IO;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// DR-09: weekly markdown archive — filename stamp, export content, no-data => no file, startup backfill.
public sealed class StandupArchiveServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonAppConfig _config;

    public StandupArchiveServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "standup-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), Path.Combine(_dir, "timesheet.db"));
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);
    }

    private static StandupEntry Entry(int id, int userId, DateOnly date, string section, string task = "Build") =>
        new(id, userId, date, section, null, "REQ-1", task, "did work", null, "Todo", 0,
            new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero));

    private (StandupArchiveService svc, Mock<IStandupRepository> repo) Make(
        IReadOnlyList<StandupEntry> all, DateOnly today, IReadOnlyList<StandupIssue>? issues = null)
    {
        var repo = new Mock<IStandupRepository>();
        repo.Setup(r => r.GetEntriesForRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>()))
            .ReturnsAsync((DateOnly from, DateOnly to) =>
                all.Where(e => e.WorkDate >= from && e.WorkDate <= to).ToList());
        repo.Setup(r => r.GetIssuesForEntriesAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(issues ?? Array.Empty<StandupIssue>());
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetAllAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true), new User(2, "Bob", null, true),
        });
        var svc = new StandupArchiveService(repo.Object, users.Object, _config, new FakeClock { Today = today });
        return (svc, repo);
    }

    [Fact]
    public void FileNameFor_uses_week_monday_stamp()
    {
        var (svc, _) = Make(Array.Empty<StandupEntry>(), new DateOnly(2026, 6, 25));
        // 2026-06-25 is a Thursday -> Monday is 2026-06-22
        Assert.Equal("20260622_daily.md", svc.FileNameFor(new DateOnly(2026, 6, 25)));
        Assert.Equal("20260622_daily.md", svc.FileNameFor(new DateOnly(2026, 6, 22)));
    }

    [Fact]
    public async Task ExportWeek_writes_markdown_with_users_and_sections()
    {
        var all = new[]
        {
            Entry(1, 1, new DateOnly(2026, 6, 22), StandupSection.Yesterday, "design"),
            Entry(2, 1, new DateOnly(2026, 6, 22), StandupSection.Today, "implement"),
            Entry(3, 2, new DateOnly(2026, 6, 23), StandupSection.Today, "review"),
        };
        var (svc, _) = Make(all, new DateOnly(2026, 6, 25));

        var path = await svc.ExportWeekAsync(new DateOnly(2026, 6, 24));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("20260622_daily.md", path);
        var md = await File.ReadAllTextAsync(path!);
        Assert.Contains("# Daily Standup — Week of 2026-06-22", md);
        Assert.Contains("### Alice", md);
        Assert.Contains("### Bob", md);
        Assert.Contains("implement", md);
        Assert.Contains("**Yesterday**", md);
        Assert.Contains("**Today**", md);
    }

    [Fact]
    public async Task ExportWeek_with_no_data_writes_no_file()
    {
        var (svc, _) = Make(Array.Empty<StandupEntry>(), new DateOnly(2026, 6, 25));
        var path = await svc.ExportWeekAsync(new DateOnly(2026, 6, 25));
        Assert.Null(path);
        Assert.False(Directory.Exists(Path.Combine(_dir, "StandupArchives"))
                     && Directory.GetFiles(Path.Combine(_dir, "StandupArchives")).Length > 0);
    }

    [Fact]
    public async Task ExportWeek_is_idempotent_overwrite()
    {
        var all = new[] { Entry(1, 1, new DateOnly(2026, 6, 22), StandupSection.Today) };
        var (svc, _) = Make(all, new DateOnly(2026, 6, 25));

        var p1 = await svc.ExportWeekAsync(new DateOnly(2026, 6, 22));
        var p2 = await svc.ExportWeekAsync(new DateOnly(2026, 6, 22));

        Assert.Equal(p1, p2);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "StandupArchives")));
    }

    [Fact]
    public async Task Backfill_generates_completed_week_with_data_but_not_current_week()
    {
        // today in week of 2026-06-22; a completed week (2026-06-15..) has data.
        var all = new[]
        {
            Entry(1, 1, new DateOnly(2026, 6, 16), StandupSection.Today),   // prev week (completed)
            Entry(2, 1, new DateOnly(2026, 6, 24), StandupSection.Today),   // current week -> skipped
        };
        var (svc, _) = Make(all, new DateOnly(2026, 6, 25));

        await svc.BackfillMissingWeeksAsync();

        var archiveDir = Path.Combine(_dir, "StandupArchives");
        Assert.True(File.Exists(Path.Combine(archiveDir, "20260615_daily.md"))); // completed week generated
        Assert.False(File.Exists(Path.Combine(archiveDir, "20260622_daily.md"))); // current week NOT generated
    }

    [Fact]
    public async Task Backfill_skips_week_that_already_has_a_file()
    {
        var archiveDir = Path.Combine(_dir, "StandupArchives");
        Directory.CreateDirectory(archiveDir);
        var existing = Path.Combine(archiveDir, "20260615_daily.md");
        await File.WriteAllTextAsync(existing, "PRE-EXISTING");

        var all = new[] { Entry(1, 1, new DateOnly(2026, 6, 16), StandupSection.Today) };
        var (svc, _) = Make(all, new DateOnly(2026, 6, 25));

        await svc.BackfillMissingWeeksAsync();

        Assert.Equal("PRE-EXISTING", await File.ReadAllTextAsync(existing)); // untouched
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
