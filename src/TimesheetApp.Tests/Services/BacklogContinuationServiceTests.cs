using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P20: "Continue on next month" — clone a backlog forward with Type="Continue", copy its tags + not-Done
// tasks (with tags), keep progress, leave the source; block a same-code duplicate in the target period.
public sealed class BacklogContinuationServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _backlogs = null!;
    private TaskRepository _tasks = null!;
    private TagRepository _tags = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _backlogs = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _tags = new TagRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; } = new(2026, 6, 15);
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public User? Current { get; set; } = new(1, "Alice", "alice", true);
        public Task<CurrentUserResult> ResolveAsync() => throw new NotSupportedException();
        public Task SetWindowsUsernameAsync(int userId, string windowsUsername) => Task.CompletedTask;
    }

    private BacklogContinuationService Svc() => new(_backlogs, _tasks, new FakeCurrentUser(), new FakeClock());

    private static Backlog B(string code, string period, string? type = "Implement",
        int? progress = null, int? teamId = 7) =>
        new(0, code, "ARCS", DateTimeOffset.UtcNow, PeriodMonth: period, Type: type,
            ProgressPercent: progress, TeamId: teamId);

    [Fact]
    public async Task Continue_clones_backlog_as_Continue_next_period_keeping_progress()
    {
        var id = await _backlogs.InsertAsync(B("PLUS-1", "2026-06", type: "Implement", progress: 60));

        var newId = await Svc().ContinueAsync(id, "2026-07");

        Assert.True(newId > 0);
        var copy = await _backlogs.GetByIdAsync(newId);
        Assert.Equal("PLUS-1", copy!.BacklogCode);
        Assert.Equal("2026-07", copy.PeriodMonth);
        Assert.Equal("Continue", copy.Type);
        Assert.Equal(60, copy.ProgressPercent);            // progress kept

        var orig = await _backlogs.GetByIdAsync(id);        // source untouched
        Assert.Equal("2026-06", orig!.PeriodMonth);
        Assert.Equal("Implement", orig.Type);
    }

    [Fact]
    public async Task Continue_copies_only_notDone_tasks()
    {
        var id = await _backlogs.InsertAsync(B("PLUS-2", "2026-06"));
        await _tasks.InsertAsync(new TaskItem(0, id, "Todo task", 0, true, "Todo"));
        await _tasks.InsertAsync(new TaskItem(0, id, "WIP task", 1, true, "In-process"));
        await _tasks.InsertAsync(new TaskItem(0, id, "Done task", 2, true, "Done"));

        var newId = await Svc().ContinueAsync(id, "2026-07");

        var copied = await _tasks.GetActiveByBacklogAsync(newId);
        Assert.Equal(2, copied.Count);
        Assert.Contains(copied, t => t.TaskName == "Todo task");
        Assert.Contains(copied, t => t.TaskName == "WIP task");
        Assert.DoesNotContain(copied, t => t.TaskName == "Done task");
    }

    [Fact]
    public async Task Continue_copies_backlog_and_task_tags()
    {
        var id = await _backlogs.InsertAsync(B("PLUS-3", "2026-06"));
        var tag = await _tags.InsertAsync(new Tag(0, "Urgent", "!", "#111111", DateTimeOffset.UtcNow));
        await _backlogs.SetTagsAsync(id, new[] { tag });
        var taskId = await _tasks.InsertAsync(new TaskItem(0, id, "T", 0, true, "Todo"));
        await _tasks.SetTaskTagsAsync(taskId, new[] { tag });

        var newId = await Svc().ContinueAsync(id, "2026-07");

        Assert.Equal(new[] { tag }, await _backlogs.GetTagIdsAsync(newId));
        var newTask = Assert.Single(await _tasks.GetActiveByBacklogAsync(newId));
        Assert.Equal(new[] { tag }, await _tasks.GetTagIdsAsync(newTask.Id));
    }

    [Fact]
    public async Task Continue_blocked_when_target_period_already_has_same_code()
    {
        var id = await _backlogs.InsertAsync(B("PLUS-4", "2026-06"));
        await _backlogs.InsertAsync(B("PLUS-4", "2026-07"));   // already continued

        var newId = await Svc().ContinueAsync(id, "2026-07");

        Assert.Equal(0, newId);
        var inJuly = (await _backlogs.SearchAsync(null, null))
            .Count(b => b.BacklogCode == "PLUS-4" && b.PeriodMonth == "2026-07");
        Assert.Equal(1, inJuly);   // no duplicate created
    }

    [Fact]
    public async Task Continue_writes_continued_audit_on_the_copy()
    {
        var id = await _backlogs.InsertAsync(B("PLUS-5", "2026-06"));

        var newId = await Svc().ContinueAsync(id, "2026-07");

        var audit = await _backlogs.GetAuditAsync(newId);
        Assert.Contains(audit, a => a.Field == "continued" && a.NewValue == "2026-06");
    }
}
