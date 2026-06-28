using System.IO;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// TL-09: monthly markdown archive — no-data => no file, moved-out section, idempotent overwrite, '|' escaped.
public sealed class TaskListArchiveServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private string _dir = null!;
    private JsonAppConfig _config = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _dir = Path.Combine(Path.GetTempPath(), "tasklist-arch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // ArchivePath drives where the service writes; DbPath only used as fallback (unused here).
        _config = new JsonAppConfig(Path.Combine(_dir, "appsettings.json"), Path.Combine(_dir, "timesheet.db"));
        _config.SetArchivePath(_dir);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 27, 8, 0, 0, TimeSpan.Zero);
    }

    private TaskListArchiveService Make(DateOnly today, ITeamRepository? teams = null)
        => new(new BacklogRepository(_db), new TaskRepository(_db), new TimeLogRepository(_db),
            new TagRepository(_db), new PcaContactRepository(_db), new UserRepository(_db),
            new HolidayRepository(_db), new ScheduleStateService(), new WorkingDayCalculator(),
            _config, new FakeClock { Today = today }, teams);

    [Fact]
    public async Task ExportMonth_with_no_data_writes_no_file()
    {
        var svc = Make(new DateOnly(2026, 6, 27));
        var path = await svc.ExportMonthAsync(2026, 5);   // empty DB -> no members
        Assert.Null(path);
        Assert.Empty(Directory.GetFiles(_dir, "*_tasklist.md"));
    }

    [Fact]
    public async Task ExportMonth_lists_current_members_and_escapes_pipe()
    {
        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(
            0, "REQ-A|B", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05",
            OfficialEstimateHours: 16m));

        var svc = Make(new DateOnly(2026, 6, 27));
        var path = await svc.ExportMonthAsync(2026, 5);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.EndsWith("202605_tasklist.md", path);
        var md = await File.ReadAllTextAsync(path!);
        Assert.Contains("# Task List — 2026-05", md);
        Assert.Contains("## Members", md);
        Assert.Contains(@"REQ-A\|B", md);          // '|' escaped
        Assert.Contains("16", md);                  // whole-number estimate
    }

    // TM-08: the archive table carries a Team column populated from the backlog's team.
    [Fact]
    public async Task ExportMonth_includes_team_column()
    {
        var teams = new TeamRepository(_db);
        var teamId = await teams.InsertAsync(new Team(0, "Team A", true, DateTimeOffset.UtcNow));

        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(
            0, "REQ-TEAM", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05", TeamId: teamId));

        var svc = Make(new DateOnly(2026, 6, 27), teams);
        var path = await svc.ExportMonthAsync(2026, 5);

        Assert.NotNull(path);
        var md = await File.ReadAllTextAsync(path!);
        Assert.Contains("| Team | Code |", md);   // header carries the Team column
        Assert.Contains("Team A", md);            // the backlog's team name appears in the row
    }

    [Fact]
    public async Task ExportMonth_includes_moved_out_section_for_a_moved_backlog()
    {
        var backlogs = new BacklogRepository(_db);
        // Created in May, then "moved" to June (writes a period_month audit old_value=2026-05).
        var id = await backlogs.InsertAsync(new Backlog(
            0, "REQ-MOVE", "ARMS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05"));
        await backlogs.UpdateAsync((await backlogs.GetByIdAsync(id))! with { PeriodMonth = "2026-06" },
            changedByUserId: 1, changedByName: "Admin");

        var svc = Make(new DateOnly(2026, 7, 1));
        var path = await svc.ExportMonthAsync(2026, 5);   // May export: backlog now in June

        Assert.NotNull(path);
        var md = await File.ReadAllTextAsync(path!);
        Assert.Contains("## Moved to next month", md);
        Assert.Contains("REQ-MOVE", md);
    }

    [Fact]
    public async Task ExportMonth_is_idempotent_overwrite()
    {
        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(0, "REQ-IDEM", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05"));

        var svc = Make(new DateOnly(2026, 6, 27));
        var p1 = await svc.ExportMonthAsync(2026, 5);
        var p2 = await svc.ExportMonthAsync(2026, 5);

        Assert.Equal(p1, p2);
        Assert.Single(Directory.GetFiles(_dir, "*_tasklist.md"));
    }

    [Fact]
    public async Task Backfill_generates_completed_month_but_not_current()
    {
        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(0, "REQ-MAY", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05"));
        await backlogs.InsertAsync(new Backlog(0, "REQ-JUN", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-06"));

        var svc = Make(new DateOnly(2026, 6, 27));   // current month = June
        await svc.BackfillMissingMonthsAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "202605_tasklist.md")));   // completed month generated
        Assert.False(File.Exists(Path.Combine(_dir, "202606_tasklist.md")));  // current month skipped
    }

    // P11 (EX-04): BuildMonthMarkdownAsync scoped to one team excludes other teams' backlogs.
    [Fact]
    public async Task BuildMonthMarkdown_scoped_to_one_team_excludes_other_teams()
    {
        var teams = new TeamRepository(_db);
        var teamA = await teams.InsertAsync(new Team(0, "Team A", true, DateTimeOffset.UtcNow));
        var teamB = await teams.InsertAsync(new Team(0, "Team B", true, DateTimeOffset.UtcNow));

        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(0, "REQ-AAA", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05", TeamId: teamA));
        await backlogs.InsertAsync(new Backlog(0, "REQ-BBB", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05", TeamId: teamB));

        var svc = Make(new DateOnly(2026, 6, 27), teams);
        var md = await svc.BuildMonthMarkdownAsync(new[] { teamA }, 2026, 5);

        Assert.NotNull(md);
        Assert.Contains("REQ-AAA", md);
        Assert.DoesNotContain("REQ-BBB", md);
    }

    [Fact]
    public async Task BuildMonthMarkdown_returns_null_when_team_has_no_data()
    {
        var teams = new TeamRepository(_db);
        var teamA = await teams.InsertAsync(new Team(0, "Team A", true, DateTimeOffset.UtcNow));
        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(0, "REQ-AAA", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05", TeamId: teamA));

        var svc = Make(new DateOnly(2026, 6, 27), teams);
        // A different (non-existent) team id => no current members, no moved-out => null.
        Assert.Null(await svc.BuildMonthMarkdownAsync(new[] { 9999 }, 2026, 5));
    }

    [Fact]
    public async Task Backfill_skips_month_that_already_has_a_file()
    {
        var existing = Path.Combine(_dir, "202605_tasklist.md");
        await File.WriteAllTextAsync(existing, "PRE-EXISTING");

        var backlogs = new BacklogRepository(_db);
        await backlogs.InsertAsync(new Backlog(0, "REQ-MAY", "ARCS", DateTimeOffset.UtcNow, PeriodMonth: "2026-05"));

        var svc = Make(new DateOnly(2026, 6, 27));
        await svc.BackfillMissingMonthsAsync();

        Assert.Equal("PRE-EXISTING", await File.ReadAllTextAsync(existing));   // untouched
    }
}
