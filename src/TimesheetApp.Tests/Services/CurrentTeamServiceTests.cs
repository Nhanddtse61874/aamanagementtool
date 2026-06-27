using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// P10 W2: ICurrentTeamService resolution + SetActiveTeamAsync contract (architecture §3, R5).
public class CurrentTeamServiceTests
{
    private static Team T(int id, string name = "T", bool active = true) =>
        new(id, name, active, DateTimeOffset.UtcNow);

    private static (CurrentTeamService Svc, Mock<IAppConfig> Cfg, WeakReferenceMessenger Bus) Build(
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
        return (new CurrentTeamService(teams.Object, cfg.Object, bus), cfg, bus);
    }

    [Fact]
    public async Task Resolves_persisted_active_team_when_still_available()
    {
        var (svc, _, _) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 20);

        await svc.InitializeAsync(1);

        Assert.Equal(20, svc.ActiveTeamId);
        Assert.Equal(20, svc.ActiveTeam!.Id);
        Assert.Equal(2, svc.AvailableTeams.Count);
    }

    [Fact]
    public async Task Falls_back_to_first_available_when_persisted_id_is_stale()
    {
        // Persisted 99 is no longer a membership -> fall back to first available (10). Never throws (R5).
        var (svc, _, _) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 99);

        await svc.InitializeAsync(1);

        Assert.Equal(10, svc.ActiveTeamId);
    }

    [Fact]
    public async Task Excludes_inactive_teams_from_available()
    {
        // User is a member of 10 + 30, but 30 is inactive (deactivated) -> not available.
        var (svc, _, _) = Build(1, new[] { 10, 30 }, new[] { T(10) }, persistedActiveTeamId: 30);

        await svc.InitializeAsync(1);

        Assert.Single(svc.AvailableTeams);
        Assert.Equal(10, svc.ActiveTeamId); // stale 30 -> first available
    }

    [Fact]
    public async Task Zero_teams_resolves_to_id_0_without_throwing()
    {
        var (svc, _, _) = Build(1, Array.Empty<int>(), Array.Empty<Team>(), persistedActiveTeamId: 0);

        await svc.InitializeAsync(1);

        Assert.Equal(0, svc.ActiveTeamId);
        Assert.Null(svc.ActiveTeam);
        Assert.Empty(svc.AvailableTeams);
    }

    [Fact]
    public async Task SetActiveTeamAsync_persists_raises_event_and_broadcasts()
    {
        var (svc, cfg, bus) = Build(1, new[] { 10, 20 }, new[] { T(10), T(20) }, persistedActiveTeamId: 10);
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
        var (svc, cfg, _) = Build(1, new[] { 10 }, new[] { T(10) }, persistedActiveTeamId: 10);
        await svc.InitializeAsync(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetActiveTeamAsync(99));
        Assert.Equal(10, svc.ActiveTeamId);
        cfg.Verify(c => c.SetActiveTeamId(99), Times.Never);
    }
}
