using CommunityToolkit.Mvvm.Messaging;
using Dapper;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

/// <summary>
/// M8.2 (Wave 4) — THE observable truth of the wave: two users sharing one database hold INDEPENDENT
/// active teams.
/// <para>
/// Before this wave the active team lived in IAppConfig, which is per-PROCESS. On the desktop that is
/// invisible (one process serves one user, so per-process == per-user). In an API one process serves
/// EVERYONE: user A switching team would move the singleton, and user B's very next request would be
/// served team A's timesheets — a cross-user data leak (violates R6), not a UI preference. Now the
/// active team lives on the user's own row (Users.active_team_id), so the two cannot collide.
/// </para>
/// <para>
/// Real repositories against a real (temp-FILE) SQLite DB via TestDb — deliberately NOT `:memory:`,
/// where every connection would get its own private database and a cross-user collision could not be
/// observed even if the bug were still present. Each CurrentTeamService stands in for one user's
/// context: two desktop processes on a shared OneDrive DB, or two request scopes in one API process.
/// </para>
/// </summary>
public sealed class CurrentTeamPerUserTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private TeamRepository _teams = null!;
    private UserRepository _users = null!;

    private int _alice, _bob, _alpha, _beta;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _teams = new TeamRepository(_db);
        _users = new UserRepository(_db);

        _alice = await _db.SeedUserAsync("Alice");
        _bob = await _db.SeedUserAsync("Bob");
        _alpha = await _db.SeedTeamAsync("Alpha");
        _beta = await _db.SeedTeamAsync("Beta");

        // Both users belong to both teams, so either team is a legal choice for either user. That is
        // what makes a leak *possible* — and therefore worth asserting against.
        foreach (var u in new[] { _alice, _bob })
            foreach (var t in new[] { _alpha, _beta })
                await _teams.AddMemberAsync(u, t);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    // A fresh service per user — its own messenger, so a broadcast from one cannot make the other's
    // re-resolve non-deterministic (the re-resolve path is driven explicitly below instead).
    private CurrentTeamService NewServiceFor(int userId, out Task initialized)
    {
        var svc = new CurrentTeamService(_teams, _users, new WeakReferenceMessenger());
        initialized = svc.InitializeAsync(userId);
        return svc;
    }

    private async Task<CurrentTeamService> ServiceForAsync(int userId)
    {
        var svc = NewServiceFor(userId, out var init);
        await init;
        return svc;
    }

    private async Task<long?> ActiveTeamInDbAsync(int userId)
    {
        using var c = _db.Create();
        return await c.ExecuteScalarAsync<long?>(
            "SELECT active_team_id FROM Users WHERE id = @id;", new { id = userId });
    }

    [Fact]
    public async Task Two_users_on_the_same_db_hold_independent_active_teams()
    {
        var aliceSvc = await ServiceForAsync(_alice);
        var bobSvc = await ServiceForAsync(_bob);

        // Neither has ever chosen a team -> both fall back to the first available one.
        Assert.Equal(_alpha, aliceSvc.ActiveTeamId);
        Assert.Equal(_alpha, bobSvc.ActiveTeamId);

        // Alice switches to Beta. Under the OLD per-process IAppConfig this wrote a single shared slot.
        await aliceSvc.SetActiveTeamAsync(_beta);

        Assert.Equal(_beta, aliceSvc.ActiveTeamId);
        Assert.Equal(_alpha, bobSvc.ActiveTeamId);   // Bob is untouched — THE point of the wave

        // And in the DB: Alice's row moved, Bob's row was never written at all.
        Assert.Equal(_beta, await ActiveTeamInDbAsync(_alice));
        Assert.Equal(0L, await ActiveTeamInDbAsync(_bob));
    }

    [Fact]
    public async Task Bobs_next_request_is_not_served_Alices_team()
    {
        var aliceSvc = await ServiceForAsync(_alice);
        await aliceSvc.SetActiveTeamAsync(_beta);

        // Bob's NEXT request / next app launch: a brand-new context resolving Bob from scratch. This is
        // the exact moment the old design leaked — the singleton config now said "Beta", so Bob would
        // have been handed Alice's team (and Alice's team-scoped timesheets) without either of them
        // doing anything wrong.
        var bobSvc = await ServiceForAsync(_bob);

        Assert.Equal(_alpha, bobSvc.ActiveTeamId);
        Assert.NotEqual(aliceSvc.ActiveTeamId, bobSvc.ActiveTeamId);
    }

    [Fact]
    public async Task Each_users_choice_persists_across_a_restart_independently()
    {
        var aliceSvc = await ServiceForAsync(_alice);
        var bobSvc = await ServiceForAsync(_bob);

        await aliceSvc.SetActiveTeamAsync(_beta);
        await bobSvc.SetActiveTeamAsync(_alpha);

        // "Restart": both contexts rebuilt from the DB. Each user gets their OWN choice back.
        var aliceAfter = await ServiceForAsync(_alice);
        var bobAfter = await ServiceForAsync(_bob);

        Assert.Equal(_beta, aliceAfter.ActiveTeamId);
        Assert.Equal(_alpha, bobAfter.ActiveTeamId);
        Assert.Equal(_beta, await ActiveTeamInDbAsync(_alice));
        Assert.Equal(_alpha, await ActiveTeamInDbAsync(_bob));
    }

    // A DataKind.Teams broadcast is the one path that reaches every live context at once (Alice's own
    // switch sends one). It must re-resolve each context against ITS OWN user — never adopt the team of
    // whoever happened to broadcast. Driven explicitly for determinism, exactly as CurrentTeamServiceTests does.
    [Fact]
    public async Task A_teams_broadcast_does_not_drag_Bob_onto_Alices_team()
    {
        var aliceSvc = await ServiceForAsync(_alice);
        var bobSvc = await ServiceForAsync(_bob);

        await aliceSvc.SetActiveTeamAsync(_beta);
        await bobSvc.OnTeamsChangedAsync();   // Bob's context reacts to Alice's broadcast

        Assert.Equal(_alpha, bobSvc.ActiveTeamId);              // still his own team
        Assert.Equal(0L, await ActiveTeamInDbAsync(_bob));      // his row was not written either
        Assert.Equal(_beta, aliceSvc.ActiveTeamId);
    }
}
