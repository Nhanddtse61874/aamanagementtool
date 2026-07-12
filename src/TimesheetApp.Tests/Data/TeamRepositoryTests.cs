using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

// P10 (TM-03): TeamRepository CRUD + UserTeams membership integration tests on the real v8 schema.
public class TeamRepositoryTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private TeamRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _repo = new TeamRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static Team New(string name) => new(0, name, true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Insert_get_byid_byname_roundtrip()
    {
        var id = await _repo.InsertAsync(New("Architect Improvement"));
        Assert.True(id > 0);

        var byId = await _repo.GetByIdAsync(id);
        Assert.Equal("Architect Improvement", byId!.Name);
        Assert.True(byId.IsActive);

        var byName = await _repo.GetByNameAsync("Architect Improvement");
        Assert.Equal(id, byName!.Id);

        Assert.Null(await _repo.GetByNameAsync("Nope"));
    }

    [Fact]
    public async Task Rename_and_softdelete_hide_from_active_but_stay_in_all()
    {
        var id = await _repo.InsertAsync(New("Team X"));

        await _repo.UpdateNameAsync(id, "Team Y");
        Assert.Equal("Team Y", (await _repo.GetByIdAsync(id))!.Name);

        await _repo.SetActiveAsync(id, false);
        Assert.Contains(await _repo.GetAllAsync(), t => t.Id == id && !t.IsActive);   // still present
        Assert.DoesNotContain(await _repo.GetActiveAsync(), t => t.Id == id);          // hidden from active
    }

    [Fact]
    public async Task SetMembers_replace_all_and_queries_both_directions()
    {
        var team = await _repo.InsertAsync(New("Squad"));
        var alice = await _db.SeedUserAsync("Alice");
        var bob = await _db.SeedUserAsync("Bob");
        var carol = await _db.SeedUserAsync("Carol");

        await _repo.SetMembersAsync(team, new[] { alice, bob });
        var members = await _repo.GetUserIdsForTeamAsync(team);
        Assert.Equal(new[] { alice, bob }.OrderBy(x => x), members.OrderBy(x => x));
        Assert.Contains(team, await _repo.GetTeamIdsForUserAsync(alice));

        // Replace-all: now only carol — alice/bob removed.
        await _repo.SetMembersAsync(team, new[] { carol });
        members = await _repo.GetUserIdsForTeamAsync(team);
        Assert.Equal(new[] { carol }, members);
        Assert.DoesNotContain(team, await _repo.GetTeamIdsForUserAsync(alice));
    }

    [Fact]
    public async Task AddMember_is_idempotent()
    {
        var team = await _repo.InsertAsync(New("Squad"));
        var alice = await _db.SeedUserAsync("Alice");

        await _repo.AddMemberAsync(alice, team);
        await _repo.AddMemberAsync(alice, team);   // re-add must not throw or duplicate

        Assert.Single(await _repo.GetUserIdsForTeamAsync(team));
    }

    // ---- M8.2 optimistic concurrency (no threads needed: the stale version IS the race window) ----

    [Fact]
    public async Task UpdateName_TwoAdmins_SecondWithStaleVersionConflicts_FirstSurvives()
    {
        var id = await _repo.InsertAsync(New("Original"));
        var loaded = await _repo.GetByIdAsync(id);
        Assert.Equal(1, loaded!.RowVersion);   // fresh insert -> v1

        // Both admins open the rename dialog and read v1. Admin A saves first.
        await _repo.UpdateNameAsync(id, "Renamed by A", loaded.RowVersion);

        // Admin B, still holding the stale v1, saves next -> conflict, not silent overwrite.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.UpdateNameAsync(id, "Renamed by B", loaded.RowVersion));
        Assert.Equal("Teams", ex.Table);
        Assert.Equal(id, ex.Id);
        Assert.Equal(1, ex.ExpectedVersion);
        Assert.False(ex.Deleted);

        var current = await _repo.GetByIdAsync(id);
        Assert.Equal("Renamed by A", current!.Name);   // A's write survives, B's is rejected
        Assert.Equal(2, current.RowVersion);
    }

    [Fact]
    public async Task UpdateName_NoExpectedVersion_IsBumpOnly_NeverThrows()
    {
        var id = await _repo.InsertAsync(New("Team X"));

        await _repo.UpdateNameAsync(id, "Team Y");   // existing 2-arg call shape: no version carried

        var after = await _repo.GetByIdAsync(id);
        Assert.Equal("Team Y", after!.Name);
        Assert.Equal(2, after.RowVersion);            // still bumped, just never checked
    }

    [Fact]
    public async Task SetActive_IsBumpOnly_NeedsNoVersionAndNeverThrows()
    {
        var id = await _repo.InsertAsync(New("Team Z"));
        Assert.Equal(1, (await _repo.GetByIdAsync(id))!.RowVersion);

        await _repo.SetActiveAsync(id, false);        // deactivation carries no version at all

        var after = await _repo.GetByIdAsync(id);
        Assert.False(after!.IsActive);
        Assert.Equal(2, after.RowVersion);             // bumped even though nothing was checked
    }

    [Fact]
    public async Task SetMembers_TwoAdmins_SecondWithStaleVersionConflicts_FirstSurvives()
    {
        var team = await _repo.InsertAsync(New("Squad"));
        var alice = await _db.SeedUserAsync("Alice");
        var bob = await _db.SeedUserAsync("Bob");
        var carol = await _db.SeedUserAsync("Carol");

        var loaded = await _repo.GetByIdAsync(team);
        Assert.Equal(1, loaded!.RowVersion);

        // Admin A (saw v1) replaces membership with {alice, bob} first.
        await _repo.SetMembersAsync(team, new[] { alice, bob }, loaded.RowVersion);

        // Admin B, also holding stale v1, tries {carol} next -> conflict; A's set is untouched.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _repo.SetMembersAsync(team, new[] { carol }, loaded.RowVersion));
        Assert.Equal("Teams", ex.Table);
        Assert.False(ex.Deleted);

        var members = await _repo.GetUserIdsForTeamAsync(team);
        Assert.Equal(new[] { alice, bob }.OrderBy(x => x), members.OrderBy(x => x));
        Assert.Equal(2, (await _repo.GetByIdAsync(team))!.RowVersion);
    }

    [Fact]
    public async Task SetMembers_NoExpectedVersion_IsBumpOnly_NeverThrowsButStillBumps()
    {
        var team = await _repo.InsertAsync(New("Squad"));
        var alice = await _db.SeedUserAsync("Alice");

        await _repo.SetMembersAsync(team, new[] { alice });   // existing 2-arg call shape

        Assert.Equal(2, (await _repo.GetByIdAsync(team))!.RowVersion);   // bumped, never checked
    }
}
