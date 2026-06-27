using Moq;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

// P10 W7 (TM-07, architecture §5b): the shared multi-team checkbox filter.
public sealed class TeamFilterViewModelTests
{
    private static Team T(int id, string name = "T") => new(id, name, true, DateTimeOffset.UtcNow);

    private static Mock<ICurrentTeamService> Team(IReadOnlyList<Team> available, int activeId)
    {
        var mock = new Mock<ICurrentTeamService>();
        mock.SetupGet(s => s.AvailableTeams).Returns(available);
        mock.SetupGet(s => s.ActiveTeamId).Returns(activeId);
        return mock;
    }

    [Fact] // default selection = the active team only
    public void Defaults_to_active_team_only_checked()
    {
        var svc = Team(new[] { T(10, "A"), T(20, "B") }, activeId: 20);
        var vm = new TeamFilterViewModel(svc.Object);

        Assert.Equal(new[] { 20 }, vm.CheckedTeamIds);
        Assert.False(vm.ShowTeamColumn);   // only one checked
    }

    [Fact] // checking a second team raises SelectionChanged and exposes both ids + the team column
    public void Checking_a_second_team_aggregates_and_signals()
    {
        var svc = Team(new[] { T(10, "A"), T(20, "B") }, activeId: 20);
        var vm = new TeamFilterViewModel(svc.Object);
        var raised = 0;
        vm.SelectionChanged += (_, _) => raised++;

        vm.Teams.First(t => t.Team.Id == 10).IsChecked = true;

        Assert.Equal(new[] { 10, 20 }, vm.CheckedTeamIds.OrderBy(x => x).ToArray());
        Assert.True(vm.ShowTeamColumn);
        Assert.Equal(1, raised);
    }

    [Fact] // ActiveTeamChanged rebuilds the list and RESETS to {new active team} (F-Q3)
    public void Active_team_change_resets_selection_to_new_active_team()
    {
        var svc = Team(new[] { T(10, "A"), T(20, "B") }, activeId: 20);
        var vm = new TeamFilterViewModel(svc.Object);
        // user expands the selection first
        vm.Teams.First(t => t.Team.Id == 10).IsChecked = true;
        Assert.Equal(2, vm.CheckedTeamIds.Count);

        var raised = 0;
        vm.SelectionChanged += (_, _) => raised++;
        // active team switches to 10
        svc.SetupGet(s => s.ActiveTeamId).Returns(10);
        svc.Raise(s => s.ActiveTeamChanged += null, EventArgs.Empty);

        Assert.Equal(new[] { 10 }, vm.CheckedTeamIds);   // reset to the new active team only
        Assert.Equal(1, raised);                          // owner gets one reload signal
    }

    [Fact] // hidden for single-team users (nothing to filter)
    public void ShowFilter_false_for_single_team_user()
    {
        var svc = Team(new[] { T(10, "A") }, activeId: 10);
        var vm = new TeamFilterViewModel(svc.Object);
        Assert.False(vm.ShowFilter);
    }

    [Fact] // shown for multi-team users
    public void ShowFilter_true_for_multi_team_user()
    {
        var svc = Team(new[] { T(10, "A"), T(20, "B") }, activeId: 10);
        var vm = new TeamFilterViewModel(svc.Object);
        Assert.True(vm.ShowFilter);
    }
}
