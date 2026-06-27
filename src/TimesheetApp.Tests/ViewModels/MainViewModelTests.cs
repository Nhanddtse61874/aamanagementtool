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
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IAppConfig> _config = new();

    private static User U(int id, string name) => new(id, name, null, true);

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
            Mock.Of<IPcaContactRepository>(), Mock.Of<IHolidayRepository>());
        var dailyReport = new DailyReportViewModel(
            Mock.Of<IStandupService>(), Mock.Of<IStandupArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger());

        // Default conflict scan target: a path with no siblings so no spurious warning fires.
        _config.SetupGet(c => c.DbPath).Returns(
            dbPath ?? Path.Combine(Path.GetTempPath(), "mvm-test-" + Guid.NewGuid().ToString("N"), "timesheet.db"));

        return new MainViewModel(
            timesheet, requests, usersVm, reports, settings, dailyReport, CreateTaskList(),
            _currentUser.Object, _users.Object, _config.Object,
            windowsUserName ?? (() => "tester"));
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

    [Fact] // XC-07: NeedsSelection -> selector invoked, chosen user persisted + set current
    public async Task NeedsSelection_invokes_selector_and_persists_choice()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null));
        var active = new[] { U(1, "Bob"), U(2, "Cara") };
        _users.Setup(u => u.GetActiveAsync()).ReturnsAsync(active);
        // After persistence, the service exposes the chosen user as Current.
        _currentUser.SetupGet(s => s.Current).Returns(U(2, "Cara"));

        IReadOnlyList<User>? offered = null;
        var vm = CreateVm(windowsUserName: () => "DESK\\cara");

        await vm.InitializeAsync(users => { offered = users; return users[1]; });

        Assert.Equal(active, offered); // active users were offered to the selector
        _currentUser.Verify(s => s.SetWindowsUsernameAsync(2, "DESK\\cara"), Times.Once);
        Assert.Equal("Cara", vm.CurrentUserName);
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

    [Fact] // XC-07: cancelling the dialog leaves no current user and persists nothing
    public async Task NeedsSelection_cancel_persists_nothing()
    {
        _currentUser.Setup(s => s.ResolveAsync())
            .ReturnsAsync(new CurrentUserResult(CurrentUserOutcome.NeedsSelection, null));
        _users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[] { U(1, "Bob") });
        var vm = CreateVm();

        await vm.InitializeAsync(_ => null); // user cancelled

        _currentUser.Verify(s => s.SetWindowsUsernameAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        Assert.Equal(string.Empty, vm.CurrentUserName);
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
            new SettingsViewModel(Mock.Of<IAppConfig>(), Mock.Of<ISettingsRepository>(), Mock.Of<ITaskTemplateRepository>(), Mock.Of<IDefaultTaskSyncService>(), Mock.Of<ITagRepository>(), Mock.Of<IPcaContactRepository>(), Mock.Of<IHolidayRepository>()),
            new DailyReportViewModel(Mock.Of<IStandupService>(), Mock.Of<IStandupArchiveService>(), Mock.Of<IClock>(), new WeakReferenceMessenger()),
            CreateTaskList(),
            _currentUser.Object, _users.Object, _config.Object, () => "tester");

        Assert.Empty(timesheet.Groups);
        await vm.ActivateTabAsync(0);
        Assert.Single(timesheet.Groups);
        Assert.Single(timesheet.Groups[0].Tasks);
        Assert.Equal("New Task", timesheet.Groups[0].Tasks[0].TaskName);
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
