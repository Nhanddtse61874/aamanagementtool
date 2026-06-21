using Xunit;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.Tests.Data;

public class RepositoryCrudTests : IAsyncLifetime
{
    private TestDb _db = null!;
    public async Task InitializeAsync() => _db = await TestDb.CreateAsync();
    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task User_insert_get_softdelete_windowsusername_roundtrip()
    {
        var repo = new UserRepository(_db);
        var id = await repo.InsertAsync(new User(0, "Alice", null, true));

        await repo.SetWindowsUsernameAsync(id, "DOMAIN\\alice");
        Assert.Equal(id, (await repo.GetByWindowsUsernameAsync("DOMAIN\\alice"))!.Id);

        await repo.SetActiveAsync(id, false);
        var all = await repo.GetAllAsync();
        var active = await repo.GetActiveAsync();
        Assert.Contains(all, u => u.Id == id && !u.IsActive);   // still present in GetAll
        Assert.DoesNotContain(active, u => u.Id == id);          // hidden from active
    }

    [Fact]
    public async Task Request_search_matches_code_or_project_and_null_returns_all()
    {
        var repo = new RequestRepository(_db);
        await repo.InsertAsync(new Request(0, "REQ-100", "Apollo", DateTimeOffset.UtcNow));
        await repo.InsertAsync(new Request(0, "REQ-200", "Gemini", DateTimeOffset.UtcNow));

        Assert.Single(await repo.SearchAsync("Apollo"));   // by project
        Assert.Single(await repo.SearchAsync("REQ-200"));  // by code
        // null => all (plus the seeded DEFAULT request from initializer)
        Assert.True((await repo.SearchAsync(null)).Count >= 2);
    }

    [Fact]
    public async Task Task_softdelete_hides_from_active_and_GetByName_finds_match()
    {
        var requests = new RequestRepository(_db);
        var tasks = new TaskRepository(_db);
        var rid = await requests.InsertAsync(new Request(0, "REQ-300", "Zeus", DateTimeOffset.UtcNow));
        var tid = await tasks.InsertAsync(new TaskItem(0, rid, "Design", 0, true));

        Assert.Equal(tid, (await tasks.GetByNameInRequestAsync(rid, "Design"))!.Id);

        await tasks.SetActiveAsync(tid, false);
        Assert.DoesNotContain(await tasks.GetActiveByRequestAsync(rid), t => t.Id == tid);
    }

    [Fact]
    public async Task Settings_set_then_get_is_insert_or_replace()
    {
        var repo = new SettingsRepository(_db);
        await repo.SetAsync("n_days", "3");
        Assert.Equal("3", await repo.GetAsync("n_days"));
        await repo.SetAsync("n_days", "5");                 // replace, not duplicate
        Assert.Equal("5", await repo.GetAsync("n_days"));
        Assert.Null(await repo.GetAsync("missing_key"));
    }
}
