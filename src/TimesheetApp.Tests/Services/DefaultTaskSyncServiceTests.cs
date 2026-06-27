using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// NOTE on P1 seeding (confirmed against DatabaseInitializer): TestDb.CreateAsync() runs the
// real initializer, which (a) ensures a hidden legacy DEFAULT request and (b) seeds DefaultTasks
// with "Annual Leave", "Meeting", "Other". The plan's draft tests assumed an empty DefaultTasks
// table; adapted here to use a NON-seeded task name ("Project Kickoff") so the materialize and
// hide->soft-delete behaviours are observable without being masked by the seeded rows. Test
// intent (DATA-03 idempotent DEFAULT, SET-04 materialize, hide->soft-delete preserving TimeLogs)
// is unchanged.
//
// P10 (W3): DEFAULT is now unique PER TEAM. SyncAsync loops over ITeamRepository.GetActiveAsync()
// and materializes the global DefaultTasks under each team's own DEFAULT backlog. The setup seeds
// one active team; tests resolve that team's DEFAULT via EnsureDefaultBacklogIdAsync(_teamId).
public class DefaultTaskSyncServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _requests = null!;
    private TaskRepository _tasks = null!;
    private DefaultTaskRepository _defaults = null!;
    private TeamRepository _teams = null!;
    private DefaultTaskSyncService _svc = null!;
    private SpyJournalWarningSink _journal = null!;
    private int _teamId;

    // Records every warning so a test can assert IsJournalGone IS consulted on the bulk path (XC-09).
    private sealed class SpyJournalWarningSink : IJournalWarningSink
    {
        public List<string> Warnings { get; } = new();
        public void Warn(string message) => Warnings.Add(message);
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _requests = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _defaults = new DefaultTaskRepository(_db);
        _teams = new TeamRepository(_db);
        _teamId = await _db.SeedTeamAsync("Team A");
        var backup = new Mock<IDbBackupHelper>();
        backup.Setup(b => b.BackupAsync()).ReturnsAsync((string?)null);
        var config = new Mock<IAppConfig>();
        config.SetupGet(c => c.DbPath).Returns(_db.Path);
        _journal = new SpyJournalWarningSink();
        _svc = new DefaultTaskSyncService(
            _requests, _tasks, _defaults, _teams, backup.Object, config.Object, _journal);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    // ---- DATA-03: exactly one DEFAULT request per team, idempotent ----
    [Fact]
    public async Task EnsureDefaultBacklog_is_idempotent_per_team()
    {
        var id1 = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var id2 = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        Assert.Equal(id1, id2);
        var backlog = (await _requests.GetByIdAsync(id1))!;
        Assert.Equal("DEFAULT", backlog.BacklogCode);
        Assert.Equal(_teamId, backlog.TeamId);
    }

    // ---- SET-04: an active DefaultTask materializes as an active Task under the team's DEFAULT ----
    [Fact]
    public async Task Sync_materializes_active_default_task_under_DEFAULT()
    {
        await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));

        await _svc.SyncAsync();

        var defReqId = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var match = await _tasks.GetByNameInBacklogAsync(defReqId, "Project Kickoff");
        Assert.NotNull(match);
        Assert.True(match!.IsActive);
    }

    // ---- SET-04 + XC-06: hide a DefaultTask -> soft-delete its Task, preserving TimeLogs ----
    [Fact]
    public async Task Sync_softdeletes_task_whose_default_was_removed_preserving_logs()
    {
        var dtId = await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));
        await _svc.SyncAsync();
        var defReqId = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var task = (await _tasks.GetByNameInBacklogAsync(defReqId, "Project Kickoff"))!;

        // Log time against it, then hide the DefaultTask and re-sync.
        var userId = await SeedUser();
        await new TimeLogRepository(_db).UpsertAsync(
            new TimeLog(0, userId, task.Id, new DateOnly(2026, 6, 16), 8m, DateTimeOffset.UtcNow));
        await _defaults.SetActiveAsync(dtId, false);

        await _svc.SyncAsync();

        var after = (await _tasks.GetByNameInBacklogAsync(defReqId, "Project Kickoff"))!;
        Assert.False(after.IsActive);                       // soft-deleted, not removed
        var logs = await new TimeLogRepository(_db).GetByUserAndRangeAsync(
            userId, new DateOnly(2026, 6, 16), new DateOnly(2026, 6, 16));
        Assert.Contains(logs, l => l.TaskId == task.Id);    // TimeLog preserved (XC-06)
    }

    // ---- decision 7: rename = soft-delete old + insert new, old TimeLogs preserved ----
    [Fact]
    public async Task Sync_rename_softdeletes_old_inserts_new_preserving_old_logs()
    {
        var dtId = await _defaults.InsertAsync(new DefaultTask(0, "Old Name", 10, true));
        await _svc.SyncAsync();
        var defReqId = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var oldTask = (await _tasks.GetByNameInBacklogAsync(defReqId, "Old Name"))!;

        var userId = await SeedUser();
        await new TimeLogRepository(_db).UpsertAsync(
            new TimeLog(0, userId, oldTask.Id, new DateOnly(2026, 6, 16), 5m, DateTimeOffset.UtcNow));

        // Rename: hide the old default-task, add a new one with the new name.
        await _defaults.SetActiveAsync(dtId, false);
        await _defaults.InsertAsync(new DefaultTask(0, "New Name", 11, true));

        await _svc.SyncAsync();

        var oldAfter = (await _tasks.GetByNameInBacklogAsync(defReqId, "Old Name"))!;
        Assert.False(oldAfter.IsActive);                    // old soft-deleted
        var newTask = await _tasks.GetByNameInBacklogAsync(defReqId, "New Name");
        Assert.NotNull(newTask);
        Assert.True(newTask!.IsActive);                     // new inserted active
        Assert.NotEqual(oldTask.Id, newTask.Id);            // distinct rows

        // Old TimeLogs preserved on the soft-deleted task (decision 7).
        var logs = await new TimeLogRepository(_db).GetByUserAndRangeAsync(
            userId, new DateOnly(2026, 6, 16), new DateOnly(2026, 6, 16));
        Assert.Contains(logs, l => l.TaskId == oldTask.Id);
    }

    // ---- idempotent: running sync twice makes no further changes ----
    [Fact]
    public async Task Sync_is_idempotent_second_run_makes_no_changes()
    {
        await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));

        await _svc.SyncAsync();
        var defReqId = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var first = await _tasks.GetActiveByBacklogAsync(defReqId);

        await _svc.SyncAsync();
        var second = await _tasks.GetActiveByBacklogAsync(defReqId);

        // Same set of active Tasks (by id + name) after a second sync — no duplicates, no churn.
        Assert.Equal(
            first.Select(t => (t.Id, t.TaskName)).OrderBy(x => x.Id),
            second.Select(t => (t.Id, t.TaskName)).OrderBy(x => x.Id));
    }

    // ---- TM-04: per-team DEFAULT isolation — two teams each get their OWN DEFAULT + tasks,
    //      no double-count, idempotent re-sync. ----
    [Fact]
    public async Task Sync_materializes_per_team_DEFAULT_isolated_and_idempotent()
    {
        var teamB = await _db.SeedTeamAsync("Team B");
        await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));

        await _svc.SyncAsync();

        var defA = await _svc.EnsureDefaultBacklogIdAsync(_teamId);
        var defB = await _svc.EnsureDefaultBacklogIdAsync(teamB);
        Assert.NotEqual(defA, defB);                        // two distinct DEFAULT backlogs

        // Each team's DEFAULT carries its own materialized task (no shared row, no double-count).
        var taskA = await _tasks.GetByNameInBacklogAsync(defA, "Project Kickoff");
        var taskB = await _tasks.GetByNameInBacklogAsync(defB, "Project Kickoff");
        Assert.NotNull(taskA);
        Assert.NotNull(taskB);
        Assert.NotEqual(taskA!.Id, taskB!.Id);

        // Re-sync creates no duplicate DEFAULT per team and no duplicate tasks.
        await _svc.SyncAsync();
        Assert.Equal(defA, await _svc.EnsureDefaultBacklogIdAsync(_teamId));
        Assert.Equal(defB, await _svc.EnsureDefaultBacklogIdAsync(teamB));
        Assert.Single(await _tasks.GetActiveByBacklogAsync(defA), t => t.TaskName == "Project Kickoff");
        Assert.Single(await _tasks.GetActiveByBacklogAsync(defB), t => t.TaskName == "Project Kickoff");
    }

    // ---- TM-04: an inactive team is skipped — its DEFAULT is not created/synced. ----
    [Fact]
    public async Task Sync_skips_inactive_teams()
    {
        var inactive = await _db.SeedTeamAsync("Dormant", isActive: false);
        await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));

        await _svc.SyncAsync();

        // No DEFAULT backlog was created for the inactive team.
        Assert.Null(await _requests.GetDefaultForTeamAsync(inactive));
    }

    // ---- FIX C1 (DATA-03/TS-02): the seeded default "Annual Leave" must surface as a Timesheet
    //      row for the active team after SyncAsync — running SyncAsync materializes the global
    //      DefaultTasks under the team's DEFAULT, which is what App.OnStartup now does per team.
    [Fact]
    public async Task Sync_after_init_materializes_seeded_default_AnnualLeave_for_timesheet()
    {
        await _svc.SyncAsync();

        var timesheetTasks = await _tasks.GetActiveForTimesheetAsync(_teamId);
        Assert.Contains(timesheetTasks, t => t.TaskName == "Annual Leave");
    }

    // ---- FIX I1 / XC-09: SyncAsync is a bulk write path and MUST consult IsJournalGone.
    //      Point IAppConfig.DbPath at a separate path that holds a lingering "<db>-journal" (no
    //      SQLite connection touches it, so SQLite won't clean it) -> IsJournalGone is false and
    //      the warning must be surfaced (never swallowed). Guards XC-09 against regressing to dead
    //      code. The real writes still go to _db (TestDb's own factory-backed connections).
    [Fact]
    public async Task Sync_warns_when_rollback_journal_persists_after_bulk_write()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tsdtsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var watchedDbPath = Path.Combine(dir, "timesheet.db");
        await File.WriteAllTextAsync(watchedDbPath + "-journal", "interrupted");
        try
        {
            var config = new Mock<IAppConfig>();
            config.SetupGet(c => c.DbPath).Returns(watchedDbPath);
            var backup = new Mock<IDbBackupHelper>();
            backup.Setup(b => b.BackupAsync()).ReturnsAsync((string?)null);
            var svc = new DefaultTaskSyncService(
                _requests, _tasks, _defaults, _teams, backup.Object, config.Object, _journal);

            await svc.SyncAsync();

            Assert.Single(_journal.Warnings);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private int? _seededUser;
    private async Task<int> SeedUser()
        => _seededUser ??= await new UserRepository(_db).InsertAsync(new User(0, "U", null, true));
}
