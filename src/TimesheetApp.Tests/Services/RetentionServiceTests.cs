using System.Data;
using Dapper;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P12 (RT-07): retention/prune core — R5a deletion, spanning-backlog survival, FK order,
// settings/reference untouched, DEFAULT survival, window-math edges, idempotency, dry-run
// write-free, snapshot guards, conflict-copy abort, marker advance, BacklogAudit retained.
public sealed class RetentionServiceTests : IAsyncLifetime, IDisposable
{
    private TestDb _db = null!;
    private string _snapDir = null!;
    private SettingsRepository _settings = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _settings = new SettingsRepository(_db);
        _snapDir = Path.Combine(Path.GetTempPath(), "ret-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_snapDir);
    }

    public Task DisposeAsync() { Dispose(); return Task.CompletedTask; }

    public void Dispose()
    {
        _db?.Dispose();
        try { if (Directory.Exists(_snapDir)) Directory.Delete(_snapDir, recursive: true); } catch { }
    }

    // ---- fakes ----------------------------------------------------------------------------------

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 28, 8, 0, 0, TimeSpan.Zero);
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

    // No-op backup helper (XC-10 step verified by other tests; here we only need it not to throw).
    private sealed class NoopBackup : IDbBackupHelper
    {
        public Task<string?> BackupAsync() => Task.FromResult<string?>(null);
    }

    // Writes a real, non-zero .db file per call (the verified snapshot path the service re-checks).
    private sealed class FakeArchiver : IPruneArchiver
    {
        private readonly string _dir;
        private readonly Func<int, int, bool>? _failPredicate;
        private readonly bool _emitZero;
        public List<(int Year, int Month)> Calls { get; } = new();

        public FakeArchiver(string dir, Func<int, int, bool>? failPredicate = null, bool emitZero = false)
        {
            _dir = dir;
            _failPredicate = failPredicate;
            _emitZero = emitZero;
        }

        public Task<string?> ArchiveMonthForPruneAsync(int year, int month)
        {
            Calls.Add((year, month));
            if (_failPredicate is not null && _failPredicate(year, month))
                return Task.FromResult<string?>(null);

            var path = Path.Combine(_dir, $"snap_{year:D4}{month:D2}.db");
            File.WriteAllBytes(path, _emitZero ? Array.Empty<byte>() : new byte[] { 1, 2, 3, 4 });
            return Task.FromResult<string?>(path);
        }
    }

    private RetentionService Make(DateOnly today, int months = 3, IPruneArchiver? archiver = null, FakeConfig? cfg = null)
    {
        cfg ??= new FakeConfig { DbPath = _db.Path, RetentionMonths = months };
        cfg.DbPath = _db.Path;
        cfg.RetentionMonths = months;
        return new RetentionService(cfg, _db, new FakeClock { Today = today }, new NoopBackup(),
            _settings, archiver ?? new FakeArchiver(_snapDir));
    }

    // ---- seed helpers (raw SQL) -----------------------------------------------------------------

    private async Task<int> SeedBacklogAsync(string code, string? periodMonth, int? teamId = null, string project = "ARCS")
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Backlogs(backlog_code, project, created_at, period_month, team_id)
              VALUES(@code, @project, @now, @pm, @teamId);
              SELECT last_insert_rowid();",
            new { code, project, now = "2026-01-01T00:00:00Z", pm = periodMonth, teamId });
    }

    private async Task<int> SeedTaskAsync(int backlogId, string name = "Impl")
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tasks(backlog_id, task_name, order_index, is_active)
              VALUES(@b, @n, 0, 1); SELECT last_insert_rowid();",
            new { b = backlogId, n = name });
    }

    private async Task SeedTimeLogAsync(int userId, int taskId, string workDate, double hours = 8)
    {
        using var c = _db.Create();
        await c.ExecuteAsync(
            @"INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at)
              VALUES(@u, @t, @d, @h, @now);",
            new { u = userId, t = taskId, d = workDate, h = hours, now = "2026-01-01T00:00:00Z" });
    }

    private async Task<int> SeedStandupEntryAsync(int userId, string workDate)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO StandupEntries(user_id, work_date, section, backlog_code, task_text, status, created_at)
              VALUES(@u, @d, 'Today', 'X', 'do', 'Todo', @now); SELECT last_insert_rowid();",
            new { u = userId, d = workDate, now = "2026-01-01T00:00:00Z" });
    }

    private async Task SeedStandupIssueAsync(int entryId)
    {
        using var c = _db.Create();
        await c.ExecuteAsync(
            @"INSERT INTO StandupIssues(entry_id, issue_text, status, created_at)
              VALUES(@e, 'broke', 'open', @now);",
            new { e = entryId, now = "2026-01-01T00:00:00Z" });
    }

    private async Task SeedBacklogTagAsync(int backlogId, int tagId)
    {
        using var c = _db.Create();
        await c.ExecuteAsync("INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@b, @t);", new { b = backlogId, t = tagId });
    }

    private async Task<int> SeedTagAsync(string text = "tag")
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<int>(
            "INSERT INTO Tags(text, icon, color, created_at) VALUES(@x, 'i', 'c', @now); SELECT last_insert_rowid();",
            new { x = text, now = "2026-01-01T00:00:00Z" });
    }

    private async Task<long> CountAsync(string sql, object? p = null)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long>(sql, p);
    }

    // ---- tests ----------------------------------------------------------------------------------

    [Fact] // cutoff math: today 2026-06, N=3 -> window {06,05,04} -> cutoff 2026-03.
    public async Task Preview_cutoff_is_first_of_month_minus_N()
    {
        var preview = await Make(new DateOnly(2026, 6, 28)).PreviewAsync();
        Assert.Equal("2026-03", preview.Cutoff);
    }

    [Fact] // window-math edge: Dec->Jan year rollover. today 2026-01, N=3 -> cutoff 2025-10.
    public async Task Cutoff_handles_year_rollover()
    {
        var preview = await Make(new DateOnly(2026, 1, 15)).PreviewAsync();
        Assert.Equal("2025-10", preview.Cutoff);
    }

    [Fact] // window-math edge: leap-year February in the window does not skew prefix compare.
    public async Task Cutoff_handles_leap_february()
    {
        // today 2024-03 (leap year), N=3 -> first(2024-03) - 3 = 2023-12.
        var preview = await Make(new DateOnly(2024, 3, 31)).PreviewAsync();
        Assert.Equal("2023-12", preview.Cutoff);
    }

    [Fact] // Preview writes nothing (read-only).
    public async Task Preview_writes_nothing()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        var t = await SeedTaskAsync(b);
        await SeedTimeLogAsync(u, t, "2026-01-10");

        var svc = Make(new DateOnly(2026, 6, 28));
        var preview = await svc.PreviewAsync();

        Assert.Contains(preview.Months, m => m.Month == "2026-01" && m.TimeLogs == 1);
        // Nothing deleted by the dry-run.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs;"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Tasks;"));
        Assert.Null(await _settings.GetAsync(RetentionService.MarkerKey));
    }

    [Fact] // Core R5a: old data deleted; in-window data + spanning backlog survive; FK order OK.
    public async Task EnsureRetention_prunes_old_keeps_window_and_spanning_backlog()
    {
        var u = await _db.SeedUserAsync();

        // Pure-old backlog: period 2026-01, only an old log -> fully pruned.
        var oldB = await SeedBacklogAsync("REQ-OLD", "2026-01");
        var oldT = await SeedTaskAsync(oldB);
        await SeedTimeLogAsync(u, oldT, "2026-01-10");

        // Spanning backlog: old period 2026-02 but a Task with an IN-WINDOW (2026-06) log -> survives.
        var spanB = await SeedBacklogAsync("REQ-SPAN", "2026-02");
        var spanT = await SeedTaskAsync(spanB);
        await SeedTimeLogAsync(u, spanT, "2026-02-05"); // old log on the same task
        await SeedTimeLogAsync(u, spanT, "2026-06-05"); // in-window log -> keeps task+backlog

        // Old standup entry + issue -> pruned.
        var e = await SeedStandupEntryAsync(u, "2026-01-15");
        await SeedStandupIssueAsync(e);
        // In-window standup entry -> survives.
        await SeedStandupEntryAsync(u, "2026-06-15");

        var svc = Make(new DateOnly(2026, 6, 28));
        var status = await svc.EnsureRetentionAsync();
        // Max pruned month with business data is 2026-02 (cutoff is 2026-03 but no data there).
        Assert.Contains("2026-02", status);

        // Old log gone, in-window logs survive (the 2026-06 + the 2026-02-? no: 2026-02 is <= cutoff so deleted).
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM TimeLogs WHERE substr(work_date,1,7) <= '2026-03';"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs WHERE work_date = '2026-06-05';"));

        // Pure-old backlog + its task gone.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE id = @id;", new { id = oldB }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Tasks WHERE id = @id;", new { id = oldT }));

        // Spanning backlog + its task SURVIVE (keep the in-window log).
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE id = @id;", new { id = spanB }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Tasks WHERE id = @id;", new { id = spanT }));

        // Standup: old entry+issue gone, in-window entry survives.
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM StandupEntries WHERE substr(work_date,1,7) <= '2026-03';"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM StandupIssues;"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM StandupEntries WHERE work_date = '2026-06-15';"));
    }

    [Fact] // RT-04: settings/reference tables byte-for-byte untouched + per-team DEFAULT survives.
    public async Task EnsureRetention_leaves_reference_tables_and_DEFAULT_untouched()
    {
        var u = await _db.SeedUserAsync("Alice");
        var team = await _db.SeedTeamAsync("Team A");
        // A per-team DEFAULT backlog (old created_at, NULL period_month) — must survive.
        int teamDefault;
        using (var c = _db.Create())
        {
            teamDefault = await c.ExecuteScalarAsync<int>(
                @"INSERT INTO Backlogs(backlog_code, project, created_at, period_month, team_id)
                  VALUES('DEFAULT', 'DEFAULT', '2026-01-01T00:00:00Z', NULL, @team);
                  SELECT last_insert_rowid();", new { team });
        }

        // Reference rows.
        using (var c = _db.Create())
        {
            await c.ExecuteAsync("INSERT INTO PcaContacts(name, is_active) VALUES('PCA', 1);");
            await c.ExecuteAsync("INSERT INTO Holidays(holiday_date, description) VALUES('2026-01-01', 'NY');");
            await c.ExecuteAsync("INSERT INTO TaskTemplates(template_name, task_name, order_index) VALUES('T', 'x', 0);");
            await c.ExecuteAsync("INSERT INTO UserTeams(user_id, team_id) VALUES(@u, @t);", new { u, t = team });
        }

        // Some old prunable data so the run actually executes a delete pass.
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        var t = await SeedTaskAsync(b);
        await SeedTimeLogAsync(u, t, "2026-01-10");

        // Snapshot counts of reference tables.
        var users = await CountAsync("SELECT COUNT(*) FROM Users;");
        var teams = await CountAsync("SELECT COUNT(*) FROM Teams;");
        var userTeams = await CountAsync("SELECT COUNT(*) FROM UserTeams;");
        var pca = await CountAsync("SELECT COUNT(*) FROM PcaContacts;");
        var holidays = await CountAsync("SELECT COUNT(*) FROM Holidays;");
        var templates = await CountAsync("SELECT COUNT(*) FROM TaskTemplates;");
        var defaultTasks = await CountAsync("SELECT COUNT(*) FROM DefaultTasks;");
        var tags = await CountAsync("SELECT COUNT(*) FROM Tags;");

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        Assert.Equal(users, await CountAsync("SELECT COUNT(*) FROM Users;"));
        Assert.Equal(teams, await CountAsync("SELECT COUNT(*) FROM Teams;"));
        Assert.Equal(userTeams, await CountAsync("SELECT COUNT(*) FROM UserTeams;"));
        Assert.Equal(pca, await CountAsync("SELECT COUNT(*) FROM PcaContacts;"));
        Assert.Equal(holidays, await CountAsync("SELECT COUNT(*) FROM Holidays;"));
        Assert.Equal(templates, await CountAsync("SELECT COUNT(*) FROM TaskTemplates;"));
        Assert.Equal(defaultTasks, await CountAsync("SELECT COUNT(*) FROM DefaultTasks;"));
        Assert.Equal(tags, await CountAsync("SELECT COUNT(*) FROM Tags;"));

        // Both the seeded team DEFAULT and the initializer's global DEFAULT survive.
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE id = @id;", new { id = teamDefault }));
        Assert.True(await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE backlog_code = 'DEFAULT';") >= 2);
    }

    [Fact] // BacklogTags: orphans for pruned backlogs cleaned; tags for surviving backlogs kept.
    public async Task EnsureRetention_cleans_orphan_backlogtags_keeps_surviving()
    {
        var u = await _db.SeedUserAsync();
        var tag = await SeedTagAsync();

        var oldB = await SeedBacklogAsync("REQ-OLD", "2026-01");
        var oldT = await SeedTaskAsync(oldB);
        await SeedTimeLogAsync(u, oldT, "2026-01-10");
        await SeedBacklogTagAsync(oldB, tag); // becomes orphan

        var spanB = await SeedBacklogAsync("REQ-SPAN", "2026-02");
        var spanT = await SeedTaskAsync(spanB);
        await SeedTimeLogAsync(u, spanT, "2026-06-05"); // in-window -> survives
        await SeedBacklogTagAsync(spanB, tag);

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM BacklogTags WHERE backlog_id = @b;", new { b = oldB }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM BacklogTags WHERE backlog_id = @b;", new { b = spanB }));
    }

    [Fact] // SUGGESTION-3 (corrected): BacklogAudit for SURVIVING backlogs is retained; audit for a
           // deleted backlog goes with it (BacklogAudit.backlog_id is a NOT NULL RESTRICT FK).
    public async Task EnsureRetention_retains_BacklogAudit_for_surviving_removes_for_deleted()
    {
        var u = await _db.SeedUserAsync();

        // Deleted backlog: only an old log -> pruned; its audit row must go (else FK error 19).
        var oldB = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(oldB), "2026-01-10");

        // Surviving (spanning) backlog: old period but an in-window log -> survives; audit retained.
        var spanB = await SeedBacklogAsync("REQ-SPAN", "2026-02");
        await SeedTimeLogAsync(u, await SeedTaskAsync(spanB), "2026-06-05");

        using (var c = _db.Create())
        {
            await c.ExecuteAsync(
                @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value, changed_at)
                  VALUES(@b, 'period_month', '2026-01', '2026-02', @now);",
                new { b = oldB, now = "2026-01-15T00:00:00Z" });
            await c.ExecuteAsync(
                @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value, changed_at)
                  VALUES(@b, 'type', 'Estimate', 'Implement', @now);",
                new { b = spanB, now = "2026-02-15T00:00:00Z" });
        }

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM BacklogAudit WHERE backlog_id = @b;", new { b = oldB }));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM BacklogAudit WHERE backlog_id = @b;", new { b = spanB }));
    }

    [Fact] // Marker advances to the max pruned month post-commit.
    public async Task EnsureRetention_advances_marker()
    {
        var u = await _db.SeedUserAsync();
        var b1 = await SeedBacklogAsync("REQ-A", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b1), "2026-01-10");
        var b2 = await SeedBacklogAsync("REQ-B", "2026-03");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b2), "2026-03-10");

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        Assert.Equal("2026-03", await _settings.GetAsync(RetentionService.MarkerKey));
    }

    [Fact] // Idempotent re-run: second run deletes nothing further (no double work / errors).
    public async Task EnsureRetention_is_idempotent()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b), "2026-01-10");

        var svc = Make(new DateOnly(2026, 6, 28));
        await svc.EnsureRetentionAsync();
        var afterFirst = await CountAsync("SELECT COUNT(*) FROM Backlogs;");

        var status2 = await svc.EnsureRetentionAsync();
        Assert.Equal(afterFirst, await CountAsync("SELECT COUNT(*) FROM Backlogs;"));
        Assert.Contains("nothing to prune", status2);
    }

    [Fact] // Post-commit XC-09: no rollback journal left behind.
    public async Task EnsureRetention_leaves_no_journal()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b), "2026-01-10");

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        Assert.False(File.Exists(_db.Path + "-journal"));
    }

    [Fact] // BLOCKER-1: archiver returns null -> that month (and later) NOT pruned.
    public async Task EnsureRetention_skips_month_when_archive_returns_null()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b), "2026-01-10");

        var archiver = new FakeArchiver(_snapDir, failPredicate: (_, _) => true);
        var status = await Make(new DateOnly(2026, 6, 28), archiver: archiver).EnsureRetentionAsync();

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs;")); // nothing deleted
        Assert.Null(await _settings.GetAsync(RetentionService.MarkerKey));
        Assert.Contains("nothing pruned", status);
    }

    [Fact] // BLOCKER-1: archiver returns a ZERO-byte snapshot -> NOT pruned.
    public async Task EnsureRetention_skips_month_when_snapshot_is_zero_bytes()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b), "2026-01-10");

        var archiver = new FakeArchiver(_snapDir, emitZero: true);
        await Make(new DateOnly(2026, 6, 28), archiver: archiver).EnsureRetentionAsync();

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs;")); // nothing deleted
        Assert.Null(await _settings.GetAsync(RetentionService.MarkerKey));
    }

    [Fact] // Contiguous-prefix: month A archives OK, month B fails -> only A pruned, B kept.
    public async Task EnsureRetention_prunes_only_contiguous_archived_prefix()
    {
        var u = await _db.SeedUserAsync();
        var bA = await SeedBacklogAsync("REQ-A", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(bA), "2026-01-10");
        var bB = await SeedBacklogAsync("REQ-B", "2026-02");
        await SeedTimeLogAsync(u, await SeedTaskAsync(bB), "2026-02-10");

        // Fail the SECOND (2026-02) month's archive; first (2026-01) succeeds.
        var archiver = new FakeArchiver(_snapDir, failPredicate: (y, m) => y == 2026 && m == 2);
        await Make(new DateOnly(2026, 6, 28), archiver: archiver).EnsureRetentionAsync();

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM TimeLogs WHERE substr(work_date,1,7) = '2026-01';"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs WHERE substr(work_date,1,7) = '2026-02';"));
        Assert.Equal("2026-01", await _settings.GetAsync(RetentionService.MarkerKey));
    }

    [Fact] // SUGGESTION-1: conflict copy present -> whole run aborts (zero archive calls, zero deletes).
    public async Task EnsureRetention_aborts_when_conflict_copy_present()
    {
        var u = await _db.SeedUserAsync();
        var b = await SeedBacklogAsync("REQ-OLD", "2026-01");
        await SeedTimeLogAsync(u, await SeedTaskAsync(b), "2026-01-10");

        // Drop a OneDrive-style conflict copy next to the live DB.
        var dir = Path.GetDirectoryName(_db.Path)!;
        var stem = Path.GetFileNameWithoutExtension(_db.Path);
        File.WriteAllText(Path.Combine(dir, $"{stem}-DESKTOP-XYZ.db"), "conflict");

        var archiver = new FakeArchiver(_snapDir);
        var status = await Make(new DateOnly(2026, 6, 28), archiver: archiver).EnsureRetentionAsync();

        Assert.Contains("conflict", status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(archiver.Calls);                                       // no archive attempted
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM TimeLogs;")); // nothing deleted
        Assert.Null(await _settings.GetAsync(RetentionService.MarkerKey));
    }

    [Fact] // Backlog with NULL period_month is never deleted (conservative guard).
    public async Task EnsureRetention_never_deletes_null_period_month_backlog()
    {
        var u = await _db.SeedUserAsync();
        // NULL period_month backlog with only an old log on it.
        var nullB = await SeedBacklogAsync("REQ-NULL", null);
        await SeedTimeLogAsync(u, await SeedTaskAsync(nullB), "2026-01-10");

        await Make(new DateOnly(2026, 6, 28)).EnsureRetentionAsync();

        // Old log deleted, but the NULL-period backlog + task remain (no orphan, not pruned).
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM TimeLogs;"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Backlogs WHERE id = @id;", new { id = nullB }));
    }
}
