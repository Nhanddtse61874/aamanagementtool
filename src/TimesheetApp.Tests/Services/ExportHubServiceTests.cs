using System.IO;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P11 (EX-02/03/05/06): the structured export hub — per-team × per-root mirror, .db copy, no-data skip,
// best-effort per root, idempotent re-run. Builders/backup are mocked (their content is covered by their
// own tests); these tests assert the orchestration + on-disk structure.
public sealed class ExportHubServiceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "exporthub-" + Guid.NewGuid().ToString("N"));
    private readonly string _root1;
    private readonly string _root2;

    public ExportHubServiceTests()
    {
        Directory.CreateDirectory(_base);
        _root1 = Path.Combine(_base, "root1");
        _root2 = Path.Combine(_base, "root2");
    }

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
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; } = new(2026, 6, 25); // Thursday; week Monday = 2026-06-22
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);
    }

    private static Team Team(int id, string name, bool active = true) =>
        new(id, name, active, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    // A hub whose builders always return content (so every period writes a file) and whose backup writes
    // a marker .db into {folder}. Tracks roots given to BackupToFolderAsync.
    private (ExportHubService hub, FakeConfig cfg, List<string> dbFolders) MakeAllData(
        IReadOnlyList<Team> teams, ISharePointDestinationValidator? spValidator = null)
    {
        var cfg = new FakeConfig { ExportRoot1Path = _root1, ExportRoot2Path = _root2 };
        var teamRepo = new Mock<ITeamRepository>();
        teamRepo.Setup(t => t.GetAllAsync()).ReturnsAsync(teams);

        var standup = new Mock<IStandupArchiveService>();
        standup.Setup(s => s.FileNameFor(It.IsAny<DateOnly>()))
            .Returns((DateOnly d) => $"{MondayOf(d):yyyyMMdd}_daily.md");
        standup.Setup(s => s.BuildWeekMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<DateOnly>()))
            .ReturnsAsync("DAILY-CONTENT");

        var tasklist = new Mock<ITaskListArchiveService>();
        tasklist.Setup(t => t.FileNameFor(It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int y, int m) => $"{y:D4}{m:D2}_tasklist.md");
        tasklist.Setup(t => t.BuildMonthMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync("TASKLIST-CONTENT");

        var export = new Mock<IExportService>();
        export.Setup(e => e.ExportMarkdownAsync(It.IsAny<ExportFilter>()))
            .ReturnsAsync("# Timesheet — 2026/06\n\n| Date | Task | Hours |\n");

        var dbFolders = new List<string>();
        var backup = new Mock<IBackupService>();
        backup.Setup(b => b.BackupToFolderAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync((string folder, int _) =>
            {
                dbFolders.Add(folder);
                Directory.CreateDirectory(folder);
                var p = Path.Combine(folder, "timesheet_stamp.db");
                File.WriteAllText(p, "DB");
                return p;
            });

        var hub = new ExportHubService(
            cfg, teamRepo.Object, standup.Object, tasklist.Object, export.Object,
            backup.Object, new FakeClock(), new PathSanitizer(), spValidator);
        return (hub, cfg, dbFolders);
    }

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    [Fact]
    public async Task ExportNow_writes_per_team_structure_mirrored_to_both_roots()
    {
        var (hub, _, _) = MakeAllData(new[] { Team(1, "Team A") });

        await hub.ExportNowAsync();

        foreach (var root in new[] { _root1, _root2 })
        {
            var teamDir = Path.Combine(root, "Team A");
            Assert.True(Directory.GetFiles(Path.Combine(teamDir, "tasklist"), "*_tasklist.md").Length > 0);
            Assert.True(Directory.GetFiles(Path.Combine(teamDir, "daily"), "*_daily.md").Length > 0);
            Assert.True(Directory.GetFiles(Path.Combine(teamDir, "timesheet"), "*_timesheet.md").Length > 0);
            // current week (Monday 2026-06-22) is present
            Assert.True(File.Exists(Path.Combine(teamDir, "daily", "20260622_daily.md")));
            // .db copy lands in {root}/db
            Assert.True(Directory.GetFiles(Path.Combine(root, "db"), "timesheet_*.db").Length > 0);
        }
    }

    [Fact]
    public async Task ExportNow_includes_inactive_teams_with_data()
    {
        var (hub, _, _) = MakeAllData(new[] { Team(2, "Ghost Team", active: false) });

        await hub.ExportNowAsync();

        Assert.True(Directory.Exists(Path.Combine(_root1, "Ghost Team", "tasklist")));
    }

    [Fact]
    public async Task ExportNow_skips_empty_root()
    {
        var (hub, cfg, _) = MakeAllData(new[] { Team(1, "Team A") });
        cfg.ExportRoot2Path = ""; // only root1 configured

        await hub.ExportNowAsync();

        Assert.True(Directory.Exists(Path.Combine(_root1, "Team A")));
        Assert.False(Directory.Exists(_root2));
    }

    [Fact]
    public async Task ExportNow_no_root_configured_is_noop()
    {
        var (hub, cfg, _) = MakeAllData(new[] { Team(1, "Team A") });
        cfg.ExportRoot1Path = "";
        cfg.ExportRoot2Path = "";

        var status = await hub.ExportNowAsync();

        Assert.Equal("no export root configured", status);
        Assert.False(Directory.Exists(_root1));
    }

    [Fact]
    public async Task ExportNow_one_failing_root_does_not_abort_the_other()
    {
        var (hub, cfg, _) = MakeAllData(new[] { Team(1, "Team A") });
        // An invalid path for root1 (illegal chars) makes that root throw; root2 must still write.
        cfg.ExportRoot1Path = "Z:\\<<<invalid|root>>>";

        var status = await hub.ExportNowAsync();

        Assert.True(Directory.Exists(Path.Combine(_root2, "Team A")));
        Assert.Contains("failed", status);
        Assert.Contains("ok", status);
    }

    [Fact]
    public async Task ExportNow_no_data_period_writes_no_file()
    {
        // Builders return null (no data) — no markdown files, but the .db copy still happens.
        var cfg = new FakeConfig { ExportRoot1Path = _root1 };
        var teamRepo = new Mock<ITeamRepository>();
        teamRepo.Setup(t => t.GetAllAsync()).ReturnsAsync(new[] { Team(1, "Team A") });

        var standup = new Mock<IStandupArchiveService>();
        standup.Setup(s => s.FileNameFor(It.IsAny<DateOnly>())).Returns("x_daily.md");
        standup.Setup(s => s.BuildWeekMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<DateOnly>()))
            .ReturnsAsync((string?)null);
        var tasklist = new Mock<ITaskListArchiveService>();
        tasklist.Setup(t => t.FileNameFor(It.IsAny<int>(), It.IsAny<int>())).Returns("x_tasklist.md");
        tasklist.Setup(t => t.BuildMonthMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((string?)null);
        var export = new Mock<IExportService>();
        export.Setup(e => e.ExportMarkdownAsync(It.IsAny<ExportFilter>()))
            .ReturnsAsync("# Timesheet — 2026/06\n\n"); // header only, no rows
        var backup = new Mock<IBackupService>();
        backup.Setup(b => b.BackupToFolderAsync(It.IsAny<string>(), It.IsAny<int>())).ReturnsAsync("db");

        var hub = new ExportHubService(
            cfg, teamRepo.Object, standup.Object, tasklist.Object, export.Object,
            backup.Object, new FakeClock(), new PathSanitizer());

        await hub.ExportNowAsync();

        var teamDir = Path.Combine(_root1, "Team A");
        Assert.False(Directory.Exists(Path.Combine(teamDir, "daily")));
        Assert.False(Directory.Exists(Path.Combine(teamDir, "tasklist")));
        Assert.False(Directory.Exists(Path.Combine(teamDir, "timesheet")));
    }

    [Fact]
    public async Task ExportNow_db_copy_lands_in_root_db_once_per_root()
    {
        var (hub, _, dbFolders) = MakeAllData(new[] { Team(1, "Team A"), Team(2, "Team B") });

        await hub.ExportNowAsync();

        // one db copy per root (not per team) => exactly 2 calls, to {root}/db.
        Assert.Equal(2, dbFolders.Count);
        Assert.Contains(Path.Combine(_root1, "db"), dbFolders);
        Assert.Contains(Path.Combine(_root2, "db"), dbFolders);
    }

    [Fact]
    public async Task ExportNow_idempotent_rerun_overwrites_same_files()
    {
        var (hub, _, _) = MakeAllData(new[] { Team(1, "Team A") });

        await hub.ExportNowAsync();
        var daily = Path.Combine(_root1, "Team A", "daily");
        var before = Directory.GetFiles(daily).Length;

        await hub.ExportNowAsync();
        var after = Directory.GetFiles(daily).Length;

        Assert.Equal(before, after); // same filenames overwritten, not duplicated
    }

    [Fact]
    public async Task Backfill_writes_completed_periods_and_skips_existing_files()
    {
        var (hub, _, _) = MakeAllData(new[] { Team(1, "Team A") });

        await hub.BackfillAsync();

        var daily = Path.Combine(_root1, "Team A", "daily");
        Assert.True(Directory.Exists(daily));
        // current week's Monday is NOT a backfill target (backfill = strictly-before-now).
        Assert.False(File.Exists(Path.Combine(daily, "20260622_daily.md")));
        // a prior completed week IS written.
        Assert.True(File.Exists(Path.Combine(daily, "20260615_daily.md")));
    }

    // B3 (M10 blocker 3): documents a pre-existing gap, not a regression the hosted-service port
    // introduces. The per-file markdown writes above are File.Exists-guarded (this class's own doc
    // comment: "idempotent re-run"), but the one whole-DB copy per root at the end of ExportRootAsync has
    // NO guard of its own — it runs on every call, backfillOnly or not. A restart calls BackfillAsync()
    // once, same as every WPF launch did, so this is the SAME exposure the desktop app already had; it is
    // recorded here so nobody mistakes "the markdown is idempotent" for "the whole job is idempotent".
    [Fact]
    public async Task Backfill_called_twice_copies_the_db_again_each_time_with_no_guard()
    {
        var (hub, _, dbFolders) = MakeAllData(new[] { Team(1, "Team A") });

        await hub.BackfillAsync();
        await hub.BackfillAsync();

        // Two calls -> two DB-copy invocations into root1/db alone (one per call), even though every
        // markdown file the second call touched already existed and was skipped.
        Assert.Equal(2, dbFolders.Count(f => f == Path.Combine(_root1, "db")));
    }

    // I-1: two team names that sanitize to the SAME folder segment must each get a distinct folder
    // (the collision gets a -{id} suffix), so neither overwrites the other.
    [Fact]
    public async Task ExportNow_distinct_teams_with_colliding_sanitized_names_get_separate_folders()
    {
        // "Team:A" -> "Team_A" (':' invalid); "Team_A" -> "Team_A" (already valid) => same segment.
        var (hub, _, _) = MakeAllData(new[] { Team(1, "Team:A"), Team(2, "Team_A") });

        await hub.ExportNowAsync();

        var clean = Path.Combine(_root1, "Team_A", "tasklist");
        var suffixed = Path.Combine(_root1, "Team_A-2", "tasklist");
        Assert.True(Directory.Exists(clean));     // first team keeps the clean segment
        Assert.True(Directory.Exists(suffixed));  // colliding team gets the -{id} suffix
        // Exactly two team folders under the root (db is separate) — no silent merge.
        var teamFolders = Directory.GetDirectories(_root1)
            .Select(Path.GetFileName)
            .Where(n => n != "db")
            .ToList();
        Assert.Equal(2, teamFolders.Count);
    }

    // SP-03: a root that fails HARD verification (a web URL here) is skipped with a clear reason — not a
    // silent success — while the other (writable) root still exports. The guard uses the real validator.
    [Fact]
    public async Task ExportNow_web_url_root_fails_with_reason_and_other_root_still_runs()
    {
        var (hub, cfg, dbFolders) = MakeAllData(new[] { Team(1, "Team A") }, new SharePointDestinationValidator());
        cfg.ExportRoot1Path = "https://contoso.sharepoint.com/sites/Team";   // hard-fail: web URL
        cfg.ExportRoot2Path = _root2;                                        // writable local root

        var status = await hub.ExportNowAsync();

        // Bad root: surfaced as a failure with the validator's actionable reason, NOT a silent "ok".
        Assert.Contains($"failed: {cfg.ExportRoot1Path}", status);
        Assert.Contains("web URL", status);
        Assert.DoesNotContain($"ok: {cfg.ExportRoot1Path}", status);

        // Good root: still ran to completion (db copy landed under it).
        Assert.Contains($"ok: {_root2}", status);
        Assert.Contains(Path.Combine(_root2, "db"), dbFolders);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); }
        catch (IOException) { }
    }
}
