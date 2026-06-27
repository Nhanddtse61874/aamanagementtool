using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

// DR-06..10: DailyReportViewModel load / add / delete / lock behavior over a mocked service.
public class DailyReportViewModelTests
{
    private static readonly DateOnly Today = new(2026, 6, 25);

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow { get; init; } = new(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);
    }

    private static (DailyReportViewModel vm, Mock<IStandupService> svc, Mock<IStandupArchiveService> arch)
        Build(DateOnly today, bool canEdit = true, UserStandup? mine = null,
              ICurrentTeamService? currentTeam = null)
    {
        var svc = new Mock<IStandupService>();
        svc.Setup(s => s.CanEditDay(It.IsAny<DateOnly>())).Returns(canEdit);
        svc.Setup(s => s.GetMyStandupAsync(It.IsAny<DateOnly>()))
            .ReturnsAsync(mine ?? new UserStandup(1, "Alice", Array.Empty<StandupEntryView>(), Array.Empty<StandupEntryView>()));
        svc.Setup(s => s.GetTeamStandupAsync(It.IsAny<DateOnly>()))
            .ReturnsAsync(Array.Empty<UserStandup>());
        svc.Setup(s => s.GetTeamStandupAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync(Array.Empty<UserStandup>());
        svc.Setup(s => s.SearchBacklogsAsync(null)).ReturnsAsync(Array.Empty<Backlog>());
        var arch = new Mock<IStandupArchiveService>();
        var vm = new DailyReportViewModel(svc.Object, arch.Object, new FakeClock { Today = today },
            new WeakReferenceMessenger(), currentTeam);
        return (vm, svc, arch);
    }

    // P10: a team service with two teams (active = 20).
    private static Mock<ICurrentTeamService> TeamSvc(int activeId = 20)
    {
        var mock = new Mock<ICurrentTeamService>();
        mock.SetupGet(s => s.AvailableTeams).Returns(new[]
        {
            new Team(10, "Alpha", true, DateTimeOffset.UtcNow),
            new Team(20, "Beta", true, DateTimeOffset.UtcNow),
        });
        mock.SetupGet(s => s.ActiveTeamId).Returns(activeId);
        return mock;
    }

    private static StandupEntryView View(int entryId, string section, bool editable = true) =>
        new(new StandupEntry(entryId, 1, Today, section, null, "REQ-1", "Build", "x", null, "Todo", 0,
                new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero)),
            Array.Empty<StandupIssue>(), editable);

    [Fact]
    public void Ctor_defaults_selected_date_to_today()
    {
        var (vm, _, _) = Build(Today);
        Assert.Equal(Today, vm.SelectedDate);
    }

    [Fact]
    public async Task Load_fills_my_sections_username_and_edit_flag()
    {
        var mine = new UserStandup(1, "Alice",
            new[] { View(10, StandupSection.Yesterday) },
            new[] { View(11, StandupSection.Today) });
        var (vm, _, _) = Build(Today, canEdit: true, mine: mine);

        await vm.LoadAsync();

        Assert.Equal("Alice", vm.MyUserName);
        Assert.Single(vm.MyYesterday);
        Assert.Single(vm.MyToday);
        Assert.True(vm.CanEditSelectedDay);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public async Task Load_on_locked_day_sets_flag_and_message()
    {
        var (vm, _, _) = Build(Today, canEdit: false);
        await vm.LoadAsync();
        Assert.False(vm.CanEditSelectedDay);
        Assert.Contains("locked", vm.StatusMessage);
    }

    [Fact]
    public async Task Add_entry_passes_section_date_and_adhoc_code_to_service()
    {
        var (vm, svc, _) = Build(Today);
        svc.Setup(s => s.AddEntryAsync(Today, It.IsAny<StandupEntryDraft>())).ReturnsAsync(5);

        vm.NewToday.BacklogCode = "ADHOC-9";
        vm.NewToday.TaskText = "spike";
        vm.NewToday.Status = "In-process";
        await vm.NewToday.AddCommand.ExecuteAsync(null);

        svc.Verify(s => s.AddEntryAsync(Today, It.Is<StandupEntryDraft>(d =>
            d.Section == StandupSection.Today && d.BacklogCode == "ADHOC-9" &&
            d.TaskText == "spike" && d.Status == "In-process" && d.BacklogId == null)), Times.Once);
    }

    [Fact]
    public async Task Add_entry_when_locked_sets_status_message()
    {
        var (vm, svc, _) = Build(Today);
        svc.Setup(s => s.AddEntryAsync(It.IsAny<DateOnly>(), It.IsAny<StandupEntryDraft>())).ReturnsAsync(0);

        vm.NewToday.BacklogCode = "X";
        vm.NewToday.TaskText = "y";
        await vm.NewToday.AddCommand.ExecuteAsync(null);

        Assert.Contains("locked", vm.StatusMessage);
    }

    [Fact]
    public async Task Delete_entry_row_calls_service()
    {
        var mine = new UserStandup(1, "Alice",
            Array.Empty<StandupEntryView>(), new[] { View(11, StandupSection.Today) });
        var (vm, svc, _) = Build(Today, mine: mine);
        svc.Setup(s => s.DeleteEntryAsync(11)).ReturnsAsync(true);

        await vm.LoadAsync();
        var row = Assert.Single(vm.MyToday);
        await row.DeleteCommand.ExecuteAsync(null);

        svc.Verify(s => s.DeleteEntryAsync(11), Times.Once);
    }

    [Fact]
    public async Task Add_issue_calls_service_with_fields()
    {
        var (vm, svc, _) = Build(Today);
        svc.Setup(s => s.AddIssueAsync(11, "blocked on vendor", "call vendor", "pending")).ReturnsAsync(1);

        await vm.AddIssueAsync(11, "blocked on vendor", "call vendor", "pending");

        svc.Verify(s => s.AddIssueAsync(11, "blocked on vendor", "call vendor", "pending"), Times.Once);
    }

    [Fact]
    public async Task Archive_week_reports_no_data()
    {
        var (vm, _, arch) = Build(Today);
        arch.Setup(a => a.ExportWeekAsync(It.IsAny<DateOnly>())).ReturnsAsync((string?)null);
        await vm.ArchiveWeekCommand.ExecuteAsync(null);
        Assert.Contains("No standup data", vm.StatusMessage);
    }

    // P10 W7 (TM-07): the board uses the teamIds overload with the default = active team only.
    [Fact]
    public async Task Board_uses_teamIds_overload_scoped_to_active_team()
    {
        var (vm, svc, _) = Build(Today, currentTeam: TeamSvc(activeId: 20).Object);
        Assert.Equal(new[] { 20 }, vm.TeamFilter!.CheckedTeamIds);

        await vm.LoadAsync();

        svc.Verify(s => s.GetTeamStandupAsync(Today,
            It.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 20 }))), Times.Once);
    }

    // P10 W7: checking a second team reloads the board with both ids.
    [Fact]
    public async Task Board_aggregates_checked_teams()
    {
        var (vm, svc, _) = Build(Today, currentTeam: TeamSvc(activeId: 20).Object);
        await vm.LoadAsync();

        vm.TeamFilter!.Teams.First(t => t.Team.Id == 10).IsChecked = true; // triggers reload
        await Task.Yield();

        svc.Verify(s => s.GetTeamStandupAsync(Today,
            It.Is<IReadOnlyList<int>>(ids => ids.OrderBy(x => x).SequenceEqual(new[] { 10, 20 }))),
            Times.AtLeastOnce);
    }
}
