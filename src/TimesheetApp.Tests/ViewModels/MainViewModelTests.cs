using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

/// <summary>
/// Unit tests for the shell VM's testable startup logic (XC-07 current-user resolution + XC-08
/// conflict-copy banner). The SelectUserDialog itself is manual-verify; here the dialog is replaced
/// by a deterministic selector delegate.
/// </summary>
public sealed class MainViewModelTests
{
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<ICurrentTeamService> _currentTeam = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAppConfig> _config = new();

    private static User U(int id, string name) => new(id, name, null, true);
    private static Team T(int id, string name) => new(id, name, true, DateTimeOffset.UtcNow);

    public MainViewModelTests()
    {
        // Default team context: no teams unless a test overrides this (Moq last-setup-wins).
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(System.Array.Empty<Team>());
    }

    // TaskListViewModel has no behaviour exercised by these shell tests; build a mock-backed instance
    // with an isolated messenger so its DataChangedMessage registration can't trigger spurious reloads.
    private static TaskListViewModel CreateTaskList() => new(
        Mock.Of<IBacklogRepository>(), Mock.Of<ITaskRepository>(), Mock.Of<ITimeLogRepository>(),
        Mock.Of<ITagRepository>(), Mock.Of<IPcaContactRepository>(), Mock.Of<IUserRepository>(),
        Mock.Of<IHolidayRepository>(), Mock.Of<IWorkingDayCalculator>(), Mock.Of<IScheduleStateService>(),
        Mock.Of<ITaskListArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger());

    // Build a MainViewModel with real (mock-backed) tab VMs. Tab loads are best-effort and isolated
    // in MainViewModel, so default-returning mocks are sufficient for these tests.
    private MainViewModel CreateVm(Func<string>? windowsUserName = null, string? dbPath = null)
    {
        var timesheet = new TimesheetViewModel(
            Mock.Of<ITimeLogService>(), Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), Mock.Of<IClock>(), () => 0);
        var requests = new BacklogsViewModel(
            Mock.Of<IBacklogRepository>(), Mock.Of<ITaskRepository>(), Mock.Of<ITaskTemplateRepository>());
        var usersVm = new UsersViewModel(Mock.Of<IUserRepository>());
        var reports = new ReportsViewModel(
            Mock.Of<ITimeLogRepository>(), Mock.Of<ITimeLogService>(), Mock.Of<ISettingsRepository>(),
            Mock.Of<IUserRepository>(), Mock.Of<IClock>(), Mock.Of<IReportAggregator>());
        var settings = new SettingsViewModel(
            Mock.Of<IAppConfig>(), Mock.Of<ISettingsRepository>(), Mock.Of<ITaskTemplateRepository>(),
            Mock.Of<IDefaultTaskSyncService>(), Mock.Of<ITagRepository>(),
            Mock.Of<IPcaContactRepository>(), Mock.Of<IHolidayRepository>(), Mock.Of<IBackupService>(),
            Mock.Of<ITeamRepository>(), Mock.Of<IUserRepository>());
        var dailyReport = new DailyReportViewModel(
            Mock.Of<IStandupService>(), Mock.Of<IStandupArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger());

        // Default conflict scan target: a path with no siblings so no spurious warning fires.
        _config.SetupGet(c => c.DbPath).Returns(
            dbPath ?? Path.Combine(Path.GetTempPath(), "mvm-test-" + Guid.NewGuid().ToString("N"), "timesheet.db"));

        // Isolated messenger so a DataKind.Teams broadcast from another test can't trigger a spurious
        // switcher refresh (the team mock default is configured in the test-class constructor).
        return new MainViewModel(
            timesheet, requests, usersVm, reports, settings, dailyReport, CreateTaskList(),
            _currentUser.Object, _currentTeam.Object, _users.Object, _teams.Object, _config.Object,
            windowsUserName ?? (() => "tester"), new WeakReferenceMessenger());
    }

    // selector that must NOT be called when the user resolves automatically
    private static User? NeverCalled(IReadOnlyList<User> _) =>
        throw new Xunit.Sdk.XunitException("selector should not be invoked when user is Resolved");

    [Fact] // XC-07: resolved current user -> CurrentUserName set, no dialog
    public async Task Resolved_user_sets_CurrentUserName_without_prompting()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.Resolved, U(7, "Alice")));
        var vm = CreateVm();

        await vm.InitializeAsync(NeverCalled);

        Assert.Equal("Alice", vm.CurrentUserName);
        _users.Verify(u => u.GetActiveAsync(), Times.Never);
    }

    [Fact] // Auto-provision: an unmapped Windows account auto-creates a NEW user even when other users exist (no picker).
    public async Task NeedsSelection_withExistingUsers_autoCreatesNewUser()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null));
        _users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[] { U(1, "Bob"), U(2, "Cara") });
        _users.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(9);
        _currentUser.SetupGet(s => s.Current).Returns(U(9, "dana"));
        var vm = CreateVm(windowsUserName: () => "dana");

        await vm.InitializeAsync(NeverCalled); // no picker, even with existing users

        _users.Verify(u => u.InsertAsync(It.Is<User>(x => x.Name == "dana" && x.IsActive)), Times.Once);
        _currentUser.Verify(s => s.SetWindowsUsernameAsync(9, "dana"), Times.Once);
        Assert.Equal("dana", vm.CurrentUserName);
    }

    [Fact] // Zero-config first run: a fresh (empty) DB auto-creates a user from the Windows name, no dialog.
    public async Task NeedsSelection_emptyDb_autoCreatesUserFromWindowsName_withoutPrompting()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null));
        _users.Setup(u => u.GetActiveAsync()).ReturnsAsync(System.Array.Empty<User>());
        _users.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(9);
        _currentUser.SetupGet(s => s.Current).Returns(U(9, "sam"));
        var vm = CreateVm(windowsUserName: () => "sam");

        await vm.InitializeAsync(NeverCalled); // selector must NOT be invoked on a fresh DB

        _users.Verify(u => u.InsertAsync(It.Is<User>(x => x.Name == "sam" && x.IsActive)), Times.Once);
        _currentUser.Verify(s => s.SetWindowsUsernameAsync(9, "sam"), Times.Once);
        Assert.Equal("sam", vm.CurrentUserName);
    }

    [Fact] // XC-08: conflict-copy sibling present -> ConflictWarning populated
    public async Task Conflict_copy_present_populates_warning()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.Resolved, U(1, "Alice")));

        var dir = Path.Combine(Path.GetTempPath(), "mvm-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "timesheet.db");
        File.WriteAllText(dbPath, "");
        File.WriteAllText(Path.Combine(dir, "timesheet-DESKTOP-AB12.db"), ""); // OneDrive conflict copy
        var vm = CreateVm(dbPath: dbPath);

        try
        {
            await vm.InitializeAsync(NeverCalled);

            Assert.False(string.IsNullOrEmpty(vm.ConflictWarning));
            Assert.Contains("timesheet-DESKTOP-AB12.db", vm.ConflictWarning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact] // Refresh bug: switching to the Timesheet tab reloads its rows so a task created elsewhere appears
    public async Task ActivateTab_reloads_timesheet_rows()
    {
        var svc = new Mock<ITimeLogService>();
        var groups = new[]
        {
            new WeekBacklogGroup(1, "REQ-1", "P",
                new[] { new WeekRow(42, "REQ-1", "New Task", 0, null, null, null, null, null) })
        };
        svc.Setup(s => s.GetWeekGroupedAsync(It.IsAny<int>(), It.IsAny<DateOnly>())).ReturnsAsync(groups);

        // Isolated messenger: the process-wide default would let OTHER test classes' broadcasts trigger
        // spurious reloads here, double-counting the groups (the historical flakiness in this test).
        var timesheet = new TimesheetViewModel(
            svc.Object, Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), Mock.Of<IClock>(),
            () => 1, new WeakReferenceMessenger());
        var vm = new MainViewModel(
            timesheet,
            new BacklogsViewModel(Mock.Of<IBacklogRepository>(), Mock.Of<ITaskRepository>(), Mock.Of<ITaskTemplateRepository>()),
            new UsersViewModel(Mock.Of<IUserRepository>()),
            new ReportsViewModel(Mock.Of<ITimeLogRepository>(), Mock.Of<ITimeLogService>(), Mock.Of<ISettingsRepository>(), Mock.Of<IUserRepository>(), Mock.Of<IClock>(), Mock.Of<IReportAggregator>()),
            new SettingsViewModel(Mock.Of<IAppConfig>(), Mock.Of<ISettingsRepository>(), Mock.Of<ITaskTemplateRepository>(), Mock.Of<IDefaultTaskSyncService>(), Mock.Of<ITagRepository>(), Mock.Of<IPcaContactRepository>(), Mock.Of<IHolidayRepository>(), Mock.Of<IBackupService>(), Mock.Of<ITeamRepository>(), Mock.Of<IUserRepository>()),
            new DailyReportViewModel(Mock.Of<IStandupService>(), Mock.Of<IStandupArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger()),
            CreateTaskList(),
            _currentUser.Object, _currentTeam.Object, _users.Object, _teams.Object, _config.Object, () => "tester", new WeakReferenceMessenger());

        Assert.Empty(timesheet.Groups);
        await vm.ActivateTabAsync(0);
        Assert.Single(timesheet.Groups);
        Assert.Single(timesheet.Groups[0].Tasks);
        Assert.Equal("New Task", timesheet.Groups[0].Tasks[0].TaskName);
    }

    // ===== W6: active-team switcher (TM-05) =====

    // Build a VM with a supplied messenger so DataKind.Teams broadcasts can be observed.
    private MainViewModel CreateVmWithMessenger(WeakReferenceMessenger messenger)
    {
        var timesheet = new TimesheetViewModel(
            Mock.Of<ITimeLogService>(), Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), Mock.Of<IClock>(), () => 0,
            new WeakReferenceMessenger());
        var requests = new BacklogsViewModel(
            Mock.Of<IBacklogRepository>(), Mock.Of<ITaskRepository>(), Mock.Of<ITaskTemplateRepository>());
        var usersVm = new UsersViewModel(Mock.Of<IUserRepository>());
        var reports = new ReportsViewModel(
            Mock.Of<ITimeLogRepository>(), Mock.Of<ITimeLogService>(), Mock.Of<ISettingsRepository>(),
            Mock.Of<IUserRepository>(), Mock.Of<IClock>(), Mock.Of<IReportAggregator>());
        var settings = new SettingsViewModel(
            Mock.Of<IAppConfig>(), Mock.Of<ISettingsRepository>(), Mock.Of<ITaskTemplateRepository>(),
            Mock.Of<IDefaultTaskSyncService>(), Mock.Of<ITagRepository>(),
            Mock.Of<IPcaContactRepository>(), Mock.Of<IHolidayRepository>(), Mock.Of<IBackupService>(),
            Mock.Of<ITeamRepository>(), Mock.Of<IUserRepository>());
        var dailyReport = new DailyReportViewModel(
            Mock.Of<IStandupService>(), Mock.Of<IStandupArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger());

        _config.SetupGet(c => c.DbPath).Returns(
            Path.Combine(Path.GetTempPath(), "mvm-test-" + Guid.NewGuid().ToString("N"), "timesheet.db"));

        return new MainViewModel(
            timesheet, requests, usersVm, reports, settings, dailyReport, CreateTaskList(),
            _currentUser.Object, _currentTeam.Object, _users.Object, _teams.Object, _config.Object,
            () => "tester", messenger);
    }

    [Fact] // TM-05: selecting a team in the switcher persists via SetActiveTeamAsync
    public void Setting_ActiveTeam_calls_SetActiveTeamAsync()
    {
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(1);
        _currentTeam.Setup(s => s.SetActiveTeamAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        var vm = CreateVm();

        vm.ActiveTeam = T(2, "Team B");

        _currentTeam.Verify(s => s.SetActiveTeamAsync(2), Times.Once);
    }

    [Fact] // Re-entrancy / no-op guards: setting the same id (or null) does NOT call SetActiveTeamAsync
    public void Setting_ActiveTeam_to_same_id_or_null_does_not_persist()
    {
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(5);
        var vm = CreateVm();

        vm.ActiveTeam = T(5, "Team A"); // same as active id -> no-op
        vm.ActiveTeam = null;           // null -> no-op

        _currentTeam.Verify(s => s.SetActiveTeamAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact] // ShowTeamSwitcher reflects team count (visible whenever the user has >=1 team)
    public void ShowTeamSwitcher_reflects_team_count()
    {
        var vm = CreateVm();

        // ISSUE 2: a single-team user (common post-migration state) must still see the switcher so the
        // current team is visible — it's a harmless no-op selector until a 2nd team exists.
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(new[] { T(1, "Only") });
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(1);
        vm.RefreshTeamsFromService();
        Assert.True(vm.ShowTeamSwitcher);
        Assert.Single(vm.AvailableTeams);

        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(new[] { T(1, "A"), T(2, "B") });
        vm.RefreshTeamsFromService();
        Assert.True(vm.ShowTeamSwitcher);
        Assert.Equal(2, vm.AvailableTeams.Count);

        // No teams (edge case) → hidden.
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(Array.Empty<Team>());
        vm.RefreshTeamsFromService();
        Assert.False(vm.ShowTeamSwitcher);
        Assert.Empty(vm.AvailableTeams);
    }

    [Fact] // RefreshTeamsFromService picks the service's active team as the selection
    public void RefreshTeamsFromService_selects_active_team()
    {
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(new[] { T(1, "A"), T(2, "B") });
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(2);
        var vm = CreateVm();

        vm.RefreshTeamsFromService();

        Assert.NotNull(vm.ActiveTeam);
        Assert.Equal(2, vm.ActiveTeam!.Id);
        // selecting via refresh must not echo back into SetActiveTeamAsync
        _currentTeam.Verify(s => s.SetActiveTeamAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact] // On DataKind.Teams broadcast the switcher list + selection refresh from the service
    public void DataKind_Teams_broadcast_refreshes_switcher()
    {
        var messenger = new WeakReferenceMessenger();
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(new[] { T(1, "A"), T(2, "B"), T(3, "C") });
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(3);
        var vm = CreateVmWithMessenger(messenger);

        messenger.Send(new DataChangedMessage(DataKind.Teams));

        Assert.Equal(3, vm.AvailableTeams.Count);
        Assert.True(vm.ShowTeamSwitcher);
        Assert.Equal(3, vm.ActiveTeam!.Id);
    }

    // ===== W9: first-run user->team join + current-team init (TM-09, architecture §6b) =====

    [Fact] // After init the resolved user is joined to the bootstrapped active team and the team service is initialized
    public async Task Init_joins_resolved_user_to_active_team_and_initializes_team_service()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.Resolved, U(7, "Alice")));
        _config.SetupGet(c => c.ActiveTeamId).Returns(3); // bootstrap persisted the active team
        var vm = CreateVm();

        await vm.InitializeAsync(NeverCalled);

        _teams.Verify(t => t.AddMemberAsync(7, 3), Times.Once);          // first-run join (idempotent)
        _currentTeam.Verify(s => s.InitializeAsync(7), Times.Once);      // team context resolved for this user
    }

    [Fact] // Fresh-DB auto-created user is also joined to the active team and the switcher is populated
    public async Task Init_freshDb_autoCreatedUser_joins_active_team_and_populates_switcher()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null));
        _users.Setup(u => u.GetActiveAsync()).ReturnsAsync(System.Array.Empty<User>());
        _users.Setup(u => u.InsertAsync(It.IsAny<User>())).ReturnsAsync(9);
        _currentUser.SetupGet(s => s.Current).Returns(U(9, "sam"));
        _config.SetupGet(c => c.ActiveTeamId).Returns(1);
        // After the join + InitializeAsync the service exposes the joined team as available + active.
        _currentTeam.SetupGet(s => s.AvailableTeams).Returns(new[] { T(1, "My Team") });
        _currentTeam.SetupGet(s => s.ActiveTeamId).Returns(1);
        var vm = CreateVm(windowsUserName: () => "sam");

        await vm.InitializeAsync(NeverCalled);

        _teams.Verify(t => t.AddMemberAsync(9, 1), Times.Once);
        _currentTeam.Verify(s => s.InitializeAsync(9), Times.Once);
        Assert.Single(vm.AvailableTeams);
        Assert.NotNull(vm.ActiveTeam);
        Assert.Equal(1, vm.ActiveTeam!.Id);
    }

    [Fact] // Unset active team (0) -> never insert a bogus membership, but still init the team service
    public async Task Init_unsetActiveTeam_skips_join_but_initializes_team_service()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.Resolved, U(4, "Dana")));
        _config.SetupGet(c => c.ActiveTeamId).Returns(0);
        var vm = CreateVm();

        await vm.InitializeAsync(NeverCalled);

        _teams.Verify(t => t.AddMemberAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _currentTeam.Verify(s => s.InitializeAsync(4), Times.Once);
    }

    [Fact] // XC-08: no sibling -> ConflictWarning empty (banner stays collapsed)
    public async Task No_conflict_copy_leaves_warning_empty()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.Resolved, U(1, "Alice")));

        var dir = Path.Combine(Path.GetTempPath(), "mvm-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "timesheet.db");
        File.WriteAllText(dbPath, "");
        var vm = CreateVm(dbPath: dbPath);

        try
        {
            await vm.InitializeAsync(NeverCalled);
            Assert.Equal(string.Empty, vm.ConflictWarning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
