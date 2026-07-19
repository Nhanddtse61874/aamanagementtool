using System.IO;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P12 (RT-03/RT-07) W2: the archive-before-prune archiver — per-team {root}/{Team}/db/{yyyyMM}_pruned.md
// to BOTH roots + a never-auto-pruned .db snapshot under {root}/db/prune-snapshots; BLOCKER-1 regression
// (the snapshot survives a later BackupService/export prune); no-root => null; no-data team => no file but
// snapshot still written; returns a verified path. Plus a small end-to-end with the REAL archiver + REAL
// RetentionService that archives then prunes a month.
public sealed class PruneArchiverTests : IDisposable
{
    private readonly string _root;        // primary export root
    private readonly string _root2;       // second export root
    private readonly string _dbDir;       // where the fake live .db lives
    private readonly string _dbPath;

    public PruneArchiverTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "prune-arch-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "root1");
        _root2 = Path.Combine(baseDir, "root2");
        _dbDir = Path.Combine(baseDir, "db");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_root2);
        Directory.CreateDirectory(_dbDir);
        _dbPath = Path.Combine(_dbDir, "timesheet.db");
        // A REAL database. This used to be six 0x09 bytes — "non-zero live db" — which passed the old
        // `Exists && Length > 0` snapshot check while being no database at all. That fixture is what
        // let File.Copy + a length check look like a verified recovery artifact.
        TinyDb.Create(_dbPath, "live-row");
    }

    public void Dispose()
    {
        var baseDir = Path.GetDirectoryName(_root)!;
        try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); } catch { }
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; } = new(2026, 6, 28);
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 28, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class StubConfig : IAppConfig
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

    // A timesheet markdown that the archiver recognizes as having data (the per-row table header).
    private const string TimesheetWithData = "# Timesheet — 2026/01\n\n| Date | Task | Hours |\n| - | - | - |\n| 2026-01-10 | Impl | 8 |\n";
    private const string TimesheetNoData = "# Timesheet — 2026/01\n\n_No entries._\n";

    private static Mock<ITeamRepository> Teams(params Team[] teams)
    {
        var m = new Mock<ITeamRepository>();
        m.Setup(t => t.GetAllAsync()).ReturnsAsync(teams);
        return m;
    }

    private PruneArchiver MakeArchiver(
        StubConfig cfg, ITeamRepository teams,
        string? taskMd = "# tasks", string? weekMd = null, string timesheetMd = TimesheetWithData)
    {
        var tasklist = new Mock<ITaskListArchiveService>();
        tasklist.Setup(t => t.BuildMonthMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(taskMd);

        var standup = new Mock<IStandupArchiveService>();
        standup.Setup(s => s.BuildWeekMarkdownAsync(It.IsAny<IReadOnlyList<int>?>(), It.IsAny<DateOnly>()))
            .ReturnsAsync(weekMd);

        var export = new Mock<IExportService>();
        export.Setup(e => e.ExportMarkdownAsync(It.IsAny<ExportFilter>())).ReturnsAsync(timesheetMd);

        return new PruneArchiver(cfg, teams, new PathSanitizer(), standup.Object, tasklist.Object,
            export.Object, new FakeClock());
    }

    // ---- tests ----------------------------------------------------------------------------------

    [Fact] // Per-team pruned md to BOTH roots + a non-zero snapshot under {root}/db/prune-snapshots; returns the path.
    public async Task Archive_writes_per_team_markdown_to_both_roots_and_a_verified_snapshot()
    {
        var cfg = new StubConfig { DbPath = _dbPath, ExportRoot1Path = _root, ExportRoot2Path = _root2 };
        var archiver = MakeArchiver(cfg, Teams(new Team(1, "Alpha", true, default)).Object);

        var snap = await archiver.ArchiveMonthForPruneAsync(2026, 1);

        Assert.NotNull(snap);
        Assert.True(File.Exists(snap));
        Assert.True(new FileInfo(snap!).Length > 0);

        // Per-team markdown landed on BOTH roots.
        Assert.True(File.Exists(Path.Combine(_root, "Alpha", "db", "202601_pruned.md")));
        Assert.True(File.Exists(Path.Combine(_root2, "Alpha", "db", "202601_pruned.md")));

        // Snapshot is in the DEDICATED prune-snapshots folder (not {root}/db directly).
        Assert.StartsWith(Path.Combine(_root, "db", "prune-snapshots"), snap!);
    }

    [Fact] // BLOCKER-1 regression: the retained snapshot SURVIVES a later BackupService.BackupNowAsync prune.
    public async Task Snapshot_survives_subsequent_BackupService_prune()
    {
        var cfg = new StubConfig { DbPath = _dbPath, ExportRoot1Path = _root, ExportRoot2Path = _root2 };
        var archiver = MakeArchiver(cfg, Teams(new Team(1, "Alpha", true, default)).Object);
        var snap = await archiver.ArchiveMonthForPruneAsync(2026, 1);
        Assert.NotNull(snap);

        // Point a BackupService at {root}/db with keep=1 (the export's own backup folder) and run it
        // several times to force a prune cycle. It prunes "timesheet_*.db" in {root}/db — NOT the
        // prune-snapshots subfolder. The snapshot must remain.
        cfg.BackupFolderPath = Path.Combine(_root, "db");
        cfg.BackupKeepCount = 1;
        var backup = new BackupService(cfg, new FakeClock());
        for (var i = 0; i < 3; i++)
            await backup.BackupToFolderAsync(Path.Combine(_root, "db"), keep: 1);

        Assert.True(File.Exists(snap), "the never-auto-pruned snapshot must survive BackupService prune");
        Assert.True(new FileInfo(snap!).Length > 0);
    }

    [Fact] // No export root configured -> returns null (caller will not prune).
    public async Task Archive_returns_null_when_no_root_configured()
    {
        var cfg = new StubConfig { DbPath = _dbPath }; // both roots empty
        var archiver = MakeArchiver(cfg, Teams(new Team(1, "Alpha", true, default)).Object);

        Assert.Null(await archiver.ArchiveMonthForPruneAsync(2026, 1));
    }

    [Fact] // A team with NO data for the month -> no file for that team, but the snapshot is still written.
    public async Task Archive_skips_no_data_team_file_but_still_writes_snapshot()
    {
        var cfg = new StubConfig { DbPath = _dbPath, ExportRoot1Path = _root };
        // All sections empty for this team: null tasklist, null week, timesheet with no data rows.
        var archiver = MakeArchiver(cfg, Teams(new Team(7, "Empty", true, default)).Object,
            taskMd: null, weekMd: null, timesheetMd: TimesheetNoData);

        var snap = await archiver.ArchiveMonthForPruneAsync(2026, 1);

        Assert.NotNull(snap);
        Assert.True(File.Exists(snap));
        Assert.False(File.Exists(Path.Combine(_root, "Empty", "db", "202601_pruned.md")));
    }

    [Fact] // M8.2: the pre-prune snapshot is the ONE artifact RetentionService trusts before it deletes
           // real rows for good. Taken from a WAL database with a hot -wal, File.Copy hands back a file
           // that exists and is non-zero but has none of the committed data in it.
    public async Task Snapshot_taken_from_a_WAL_database_is_a_complete_verified_database()
    {
        using var live = TinyDb.OpenWal(_dbPath);        // open -> the WAL is never checkpointed
        TinyDb.Seed(live, "alpha", "beta", "gamma");     // committed, and living in the -wal

        var cfg = new StubConfig { DbPath = _dbPath, ExportRoot1Path = _root };
        var archiver = MakeArchiver(cfg, Teams(new Team(1, "Alpha", true, default)).Object);

        var snap = await archiver.ArchiveMonthForPruneAsync(2026, 1);

        Assert.NotNull(snap);
        Assert.Equal("ok", TinyDb.IntegrityCheck(snap!));
        Assert.Equal(new[] { "live-row", "alpha", "beta", "gamma" }, TinyDb.ReadAll(snap!));
    }

    [Fact] // Inactive teams are included (GetAllAsync), and a team WITH data gets a file.
    public async Task Archive_includes_inactive_team_with_data()
    {
        var cfg = new StubConfig { DbPath = _dbPath, ExportRoot1Path = _root };
        var archiver = MakeArchiver(cfg, Teams(new Team(3, "Gone", IsActive: false, default)).Object);

        await archiver.ArchiveMonthForPruneAsync(2026, 1);

        Assert.True(File.Exists(Path.Combine(_root, "Gone", "db", "202601_pruned.md")));
    }

    // ---- end-to-end: REAL PruneArchiver + REAL RetentionService ---------------------------------

    [Fact] // RetentionService with the REAL archiver actually archives then prunes; snapshot+md exist, rows gone.
    public async Task EndToEnd_real_archiver_prunes_month_and_keeps_recovery_artifacts()
    {
        using var db = await TestDb.CreateAsync();

        // Seed a team + a pure-old (2026-01) backlog/task/log -> should be archived then pruned.
        var team = await db.SeedTeamAsync("E2E");
        var user = await db.SeedUserAsync();
        int backlog;
        using (var c = db.Create())
        {
            backlog = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                @"INSERT INTO Backlogs(backlog_code, project, created_at, period_month, team_id)
                  VALUES('REQ-OLD', 'ARCS', '2026-01-01T00:00:00Z', '2026-01', @team);
                  SELECT last_insert_rowid();", new { team });
        }
        var task = await db.SeedTaskAsync(backlog);
        using (var c = db.Create())
        {
            await Dapper.SqlMapper.ExecuteAsync(c,
                @"INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at)
                  VALUES(@u, @t, '2026-01-10', 8, '2026-01-01T00:00:00Z');", new { u = user, t = task });
        }

        var cfg = new StubConfig { DbPath = db.Path, ExportRoot1Path = _root, RetentionMonths = 3 };

        // REAL archiver, but with stub builders that emit real markdown (the DB-row deletion is what's
        // under test; the markdown builders are exercised by their own P11 tests).
        var archiver = MakeArchiver(cfg, Teams(new Team(team, "E2E", true, default)).Object);

        var clock = new FakeClock { Today = new DateOnly(2026, 6, 28) };
        var settings = new SettingsRepository(db);
        var service = new RetentionService(cfg, db, clock, NoopBackupHelper.Instance, settings, archiver);

        var status = await service.EnsureRetentionAsync();

        // The month was pruned.
        Assert.Contains("2026-01", status);
        using (var c = db.Create())
        {
            Assert.Equal(0L, await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, "SELECT COUNT(*) FROM TimeLogs;"));
            Assert.Equal(0L, await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
                "SELECT COUNT(*) FROM Backlogs WHERE id = @id;", new { id = backlog }));
        }

        // Recovery artifacts on disk: per-team markdown + the never-pruned snapshot.
        Assert.True(File.Exists(Path.Combine(_root, "E2E", "db", "202601_pruned.md")));
        var snapDir = Path.Combine(_root, "db", "prune-snapshots");
        Assert.True(Directory.Exists(snapDir));
        Assert.Contains(Directory.EnumerateFiles(snapDir, "*.db"), f => new FileInfo(f).Length > 0);

        // Marker advanced.
        Assert.Equal("2026-01", await settings.GetAsync(RetentionService.MarkerKey));
    }

    private sealed class NoopBackupHelper : IDbBackupHelper
    {
        public static readonly NoopBackupHelper Instance = new();
        public Task<string?> BackupAsync() => Task.FromResult<string?>(null);
    }
}
