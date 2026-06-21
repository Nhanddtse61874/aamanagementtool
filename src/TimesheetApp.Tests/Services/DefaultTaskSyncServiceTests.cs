using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// NOTE on P1 seeding (confirmed against DatabaseInitializer): TestDb.CreateAsync() runs the
// real initializer, which (a) ensures the hidden DEFAULT request and (b) seeds DefaultTasks
// with "Annual Leave", "Meeting", "Other". The plan's draft tests assumed an empty DefaultTasks
// table; adapted here to use a NON-seeded task name ("Project Kickoff") so the materialize and
// hide->soft-delete behaviours are observable without being masked by the seeded rows. Test
// intent (DATA-03 idempotent DEFAULT, SET-04 materialize, hide->soft-delete preserving TimeLogs)
// is unchanged.
public class DefaultTaskSyncServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private RequestRepository _requests = null!;
    private TaskRepository _tasks = null!;
    private DefaultTaskRepository _defaults = null!;
    private DefaultTaskSyncService _svc = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _requests = new RequestRepository(_db);
        _tasks = new TaskRepository(_db);
        _defaults = new DefaultTaskRepository(_db);
        var backup = new Mock<IDbBackupHelper>();
        backup.Setup(b => b.BackupAsync()).ReturnsAsync((string?)null);
        _svc = new DefaultTaskSyncService(_requests, _tasks, _defaults, backup.Object);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    // ---- DATA-03: exactly one DEFAULT request, idempotent ----
    [Fact]
    public async Task EnsureDefaultRequest_is_idempotent()
    {
        var id1 = await _svc.EnsureDefaultRequestIdAsync();
        var id2 = await _svc.EnsureDefaultRequestIdAsync();
        Assert.Equal(id1, id2);
        Assert.Equal("DEFAULT", (await _requests.GetByIdAsync(id1))!.RequestCode);
    }

    // ---- SET-04: an active DefaultTask materializes as an active Task under DEFAULT ----
    [Fact]
    public async Task Sync_materializes_active_default_task_under_DEFAULT()
    {
        await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));

        await _svc.SyncAsync();

        var defReqId = await _svc.EnsureDefaultRequestIdAsync();
        var match = await _tasks.GetByNameInRequestAsync(defReqId, "Project Kickoff");
        Assert.NotNull(match);
        Assert.True(match!.IsActive);
    }

    // ---- SET-04 + XC-06: hide a DefaultTask -> soft-delete its Task, preserving TimeLogs ----
    [Fact]
    public async Task Sync_softdeletes_task_whose_default_was_removed_preserving_logs()
    {
        var dtId = await _defaults.InsertAsync(new DefaultTask(0, "Project Kickoff", 10, true));
        await _svc.SyncAsync();
        var defReqId = await _svc.EnsureDefaultRequestIdAsync();
        var task = (await _tasks.GetByNameInRequestAsync(defReqId, "Project Kickoff"))!;

        // Log time against it, then hide the DefaultTask and re-sync.
        var userId = await SeedUser();
        await new TimeLogRepository(_db).UpsertAsync(
            new TimeLog(0, userId, task.Id, new DateOnly(2026, 6, 16), 8m, DateTimeOffset.UtcNow));
        await _defaults.SetActiveAsync(dtId, false);

        await _svc.SyncAsync();

        var after = (await _tasks.GetByNameInRequestAsync(defReqId, "Project Kickoff"))!;
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
        var defReqId = await _svc.EnsureDefaultRequestIdAsync();
        var oldTask = (await _tasks.GetByNameInRequestAsync(defReqId, "Old Name"))!;

        var userId = await SeedUser();
        await new TimeLogRepository(_db).UpsertAsync(
            new TimeLog(0, userId, oldTask.Id, new DateOnly(2026, 6, 16), 5m, DateTimeOffset.UtcNow));

        // Rename: hide the old default-task, add a new one with the new name.
        await _defaults.SetActiveAsync(dtId, false);
        await _defaults.InsertAsync(new DefaultTask(0, "New Name", 11, true));

        await _svc.SyncAsync();

        var oldAfter = (await _tasks.GetByNameInRequestAsync(defReqId, "Old Name"))!;
        Assert.False(oldAfter.IsActive);                    // old soft-deleted
        var newTask = await _tasks.GetByNameInRequestAsync(defReqId, "New Name");
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
        var defReqId = await _svc.EnsureDefaultRequestIdAsync();
        var first = await _tasks.GetActiveByRequestAsync(defReqId);

        await _svc.SyncAsync();
        var second = await _tasks.GetActiveByRequestAsync(defReqId);

        // Same set of active Tasks (by id + name) after a second sync — no duplicates, no churn.
        Assert.Equal(
            first.Select(t => (t.Id, t.TaskName)).OrderBy(x => x.Id),
            second.Select(t => (t.Id, t.TaskName)).OrderBy(x => x.Id));
    }

    private int? _seededUser;
    private async Task<int> SeedUser()
        => _seededUser ??= await new UserRepository(_db).InsertAsync(new User(0, "U", null, true));
}
