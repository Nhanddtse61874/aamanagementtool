using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P10 W2: ICurrentTeamService resolution + SetActiveTeamAsync contract (architecture Â§3, R5).
public class CurrentTeamServiceTests
{
    private static Team T(int id, string name = "T", bool active = true) =>
        new(id, name, active, DateTimeOffset.UtcNow);

    private static (CurrentTeamService Svc, Mock<IAppConfig> Cfg, WeakReferenceMessenger Bus, Mock<ITeamRepository> Teams) Build(
        int userId,
        IReadOnlyList<int> userTeamIds,
        IReadOnlyList<Team> activeTeams,
        int persistedActiveTeamId)
    {
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetTeamIdsForUserAsync(userId)).ReturnsAsync(userTeamIds);
        teams.Setup(t => t.GetActiveAsync()).ReturnsAsync(activeTeams);

        var cfg = new Mock<IAppConfig>();
        cfg.SetupGet(c => c.ActiveTeamId).Returns(persistedActiveTeamId);
        cfg.Setup(c => c.SetActiveTeamId(It.IsAny<int>()))
            .Callback<int>(id => cfg.SetupGet(c => c.ActiveTeamId).Returns(id));

        var bus = new WeakReferenceMessenger();
        return (new CurrentTeamService(teams.Object, cfg.Object, bus), cfg, bus, teams);
    }

    [Fact]
    public async Task Resolves_persisted_active_team_when_still_available()
    {
        var (svc, _, _, _) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 20);

        await svc.InitializeAsync(1);

        Assert.Equal(20, svc.ActiveTeamId);
        Assert.Equal(20, svc.ActiveTeam!.Id);
        Assert.Equal(2, svc.AvailableTeams.Count);
    }

    [Fact]
    public async Task Falls_back_to_first_available_when_persisted_id_is_stale()
    {
        // Persisted 99 is no longer a membership -> fall back to first available (10). Never throws (R5).
        var (svc, _, _, _) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 99);

        await svc.InitializeAsync(1);

        Assert.Equal(10, svc.ActiveTeamId);
    }

    [Fact]
    public async Task Excludes_inactive_teams_from_available()
    {
        // User is a member of 10 + 30, but 30 is inactive (deactivated) -> not available.
        var (svc, _, _, _) = Build(1, new[] { 10, 30 }, new[] { T(10) }, persistedActiveTeamId: 30);

        await svc.InitializeAsync(1);

        Assert.Single(svc.AvailableTeams);
        Assert.Equal(10, svc.ActiveTeamId); // stale 30 -> first available
    }

    [Fact]
    public async Task Zero_teams_resolves_to_id_0_without_throwing()
    {
        var (svc, _, _, _) = Build(1, Array.Empty<int>(), Array.Empty<Team>(), persistedActiveTeamId: 0);

        await svc.InitializeAsync(1);

        Assert.Equal(0, svc.ActiveTeamId);
        Assert.Null(svc.ActiveTeam);
        Assert.Empty(svc.AvailableTeams);
    }

    [Fact]
    public async Task SetActiveTeamAsync_persists_raises_event_and_broadcasts()
    {
        var (svc, cfg, bus, _) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 10);
        await svc.InitializeAsync(1);

        var raised = false;
        svc.ActiveTeamChanged += (_, _) => raised = true;
        var got = 0;
        bus.Register<DataChangedMessage>(this, (_, m) => { if (m.Kind == DataKind.Teams) got++; });

        await svc.SetActiveTeamAsync(20);

        Assert.Equal(20, svc.ActiveTeamId);
        cfg.Verify(c => c.SetActiveTeamId(20), Times.Once);
        Assert.True(raised);
        Assert.Equal(1, got);
    }

    [Fact]
    public async Task SetActiveTeamAsync_rejects_a_team_outside_membership()
    {
        var (svc, cfg, _, _) = Build(1, new[] { 10 }, new[] { T(10) }, persistedActiveTeamId: 10);
        await svc.InitializeAsync(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetActiveTeamAsync(99));
        Assert.Equal(10, svc.ActiveTeamId);
        cfg.Verify(c => c.SetActiveTeamId(99), Times.Never);
    }

    // I3: a DataKind.Teams broadcast (e.g. new membership) re-resolves AvailableTeams live; an active
    // team that is still valid stays put (no event, no churn).
    [Fact]
    public async Task Teams_broadcast_refreshes_available_and_keeps_valid_active()
    {
        var (svc, _, _, teams) = Build(1, new[] { 10 }, new[] { T(10) }, persistedActiveTeamId: 10);
        await svc.InitializeAsync(1);
        Assert.Single(svc.AvailableTeams);
        Assert.Equal(10, svc.ActiveTeamId);

        // Settings added the user to a new team 20 -> reflect it on the next broadcast.
        teams.Setup(t => t.GetTeamIdsForUserAsync(1)).ReturnsAsync(new[] { 10, 20 });
        teams.Setup(t => t.GetActiveAsync()).ReturnsAsync(new[] { T(10), T(20) });
        var raised = false;
        svc.ActiveTeamChanged += (_, _) => raised = true;

        await svc.OnTeamsChangedAsync();

        Assert.Equal(2, svc.AvailableTeams.Count);   // refreshed without a restart
        Assert.Equal(10, svc.ActiveTeamId);          // still valid -> unchanged
        Assert.False(raised);                        // no needless churn
    }

    // I3: when the active team is deactivated/removed, a DataKind.Teams broadcast falls the active
    // team back to the first remaining available team and raises ActiveTeamChanged.
    [Fact]
    public async Task Teams_broadcast_falls_back_when_active_team_deactivated()
    {
        var (svc, cfg, _, teams) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 10);
        await svc.InitializeAsync(1);
        Assert.Equal(10, svc.ActiveTeamId);

        // Team 10 deactivated elsewhere -> only 20 remains active for the user.
        teams.Setup(t => t.GetActiveAsync()).ReturnsAsync(new[] { T(20) });
        var raised = false;
        svc.ActiveTeamChanged += (_, _) => raised = true;

        // Drive the re-resolve directly (deterministic); the bus subscription is wired in the ctor and
        // simply forwards DataKind.Teams to this same method.
        await svc.OnTeamsChangedAsync();

        Assert.Single(svc.AvailableTeams);
        Assert.Equal(20, svc.ActiveTeamId);          // fell back to the remaining team
        Assert.True(raised);
        cfg.Verify(c => c.SetActiveTeamId(20), Times.Once);
    }
}
