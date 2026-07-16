using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

// Uses the TestDb fixture (real P1 schema). Note: the initializer SEEDS DefaultTasks with
// "Annual Leave", "Meeting", "Other" (see DefaultTaskSyncServiceTests' note), so this test uses a
// NON-seeded name ("Retro") and deactivates it — the observable difference between GetAllAsync and
// GetActiveAsync is that this row survives in the former and is absent from the latter.
public sealed class DefaultTaskRepositoryTests
{
    [Fact] // SET-05: GetAllAsync returns an INACTIVE row that GetActiveAsync omits — the only thing the
           // Settings screen needs in order to re-activate a deactivated default task.
    public async Task GetAllAsync_returns_an_inactive_row_that_GetActiveAsync_omits()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new DefaultTaskRepository(db);
        var id = await repo.InsertAsync(new DefaultTask(0, "Retro", 10, true));
        await repo.SetActiveAsync(id, false);            // soft-delete it

        var all = await repo.GetAllAsync();
        var active = await repo.GetActiveAsync();

        var row = Assert.Single(all, d => d.Id == id);
        Assert.False(row.IsActive);                      // GetAll keeps the inactive row...
        Assert.DoesNotContain(active, d => d.Id == id);  // ...and GetActive drops it (the reason /all exists)
    }
}
