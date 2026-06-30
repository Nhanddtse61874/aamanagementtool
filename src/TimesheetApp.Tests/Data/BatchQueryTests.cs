using Xunit;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Data;

// Batch / targeted query round-trips for the N+1-killing repository methods:
// TaskRepository.GetActiveByBacklogsAsync (active tasks for many backlogs in one grouped query),
// HolidayRepository.IsHolidayAsync (single-date existence probe), and
// BacklogRepository.GetAuditForBacklogsAsync (audit history for many backlogs in one grouped query).
public class BatchQueryTests : IAsyncLifetime
{
    private TestDb _db = null!;
    public async Task InitializeAsync() => _db = await TestDb.CreateAsync();
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task GetActiveByBacklogsAsync_groups_active_tasks_per_backlog_in_one_shape()
    {
        var tasks = new TaskRepository(_db);
        var b1 = await _db.SeedRequestAsync("REQ-B1", "ARCS");
        var b2 = await _db.SeedRequestAsync("REQ-B2", "ARCS");

        // b1 gets two active tasks (ordered) + one inactive (must be excluded); b2 gets one active.
        var t1a = await _db.SeedTaskAsync(b1, "B1-First", orderIndex: 0);
        var t1b = await _db.SeedTaskAsync(b1, "B1-Second", orderIndex: 1);
        await _db.SeedTaskAsync(b1, "B1-Gone", orderIndex: 2, isActive: false);
        var t2a = await _db.SeedTaskAsync(b2, "B2-Only", orderIndex: 0);

        var byBacklog = await tasks.GetActiveByBacklogsAsync(new[] { b1, b2 });

        // b1: exactly the two active tasks, in order_index order; inactive task excluded.
        Assert.Equal(new[] { t1a, t1b }, byBacklog[b1].Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "B1-First", "B1-Second" }, byBacklog[b1].Select(t => t.TaskName).ToArray());
        Assert.All(byBacklog[b1], t => Assert.True(t.IsActive));

        // b2: exactly its single active task.
        Assert.Equal(new[] { t2a }, byBacklog[b2].Select(t => t.Id).ToArray());

        // A backlog with no matching tasks is simply an absent key (callers use TryGetValue).
        Assert.False(byBacklog.TryGetValue(999999, out _));
    }

    [Fact]
    public async Task IsHolidayAsync_true_for_marked_date_false_otherwise()
    {
        var repo = new HolidayRepository(_db);
        var marked = new DateOnly(2026, 7, 1);

        Assert.False(await repo.IsHolidayAsync(marked));   // not yet marked

        await repo.UpsertAsync(marked, "National Day");
        Assert.True(await repo.IsHolidayAsync(marked));                       // marked date
        Assert.False(await repo.IsHolidayAsync(new DateOnly(2026, 7, 2)));    // a different date
    }

    [Fact]
    public async Task GetAuditForBacklogsAsync_groups_audit_rows_per_backlog()
    {
        var repo = new BacklogRepository(_db);
        var b1 = await _db.SeedRequestAsync("REQ-A1", "ARCS");
        var b2 = await _db.SeedRequestAsync("REQ-A2", "ARCS");

        // Audit rows are written by UpdateAsync on a changed field. Change progress on each backlog.
        await repo.UpdateAsync((await repo.GetByIdAsync(b1))! with { ProgressPercent = 25 },
            changedByUserId: 1, changedByName: "Amy");
        await repo.UpdateAsync((await repo.GetByIdAsync(b2))! with { ProgressPercent = 50 },
            changedByUserId: 2, changedByName: "Ben");

        var byBacklog = await repo.GetAuditForBacklogsAsync(new[] { b1, b2 });

        Assert.Contains(byBacklog[b1], a => a.Field == "progress_percent" && a.NewValue == "25");
        Assert.All(byBacklog[b1], a => Assert.Equal(b1, a.BacklogId));
        Assert.Contains(byBacklog[b2], a => a.Field == "progress_percent" && a.NewValue == "50");
        Assert.All(byBacklog[b2], a => Assert.Equal(b2, a.BacklogId));

        // A backlog with no audit history is simply an absent key.
        Assert.False(byBacklog.TryGetValue(999999, out _));
    }
}
