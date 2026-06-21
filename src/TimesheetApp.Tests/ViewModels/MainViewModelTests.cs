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

    // Build a MainViewModel with real (mock-backed) tab VMs. Tab loads are best-effort and isolated
    // in MainViewModel, so default-returning mocks are sufficient for these tests.
    private MainViewModel CreateVm(Func<string>? windowsUserName = null, string? dbPath = null)
    {
        var timesheet = new TimesheetViewModel(
            Mock.Of<ITimeLogService>(), Mock.Of<ISmartInputService>(), Mock.Of<IClock>(), () => 0);
        var requests = new RequestsViewModel(
            Mock.Of<IRequestRepository>(), Mock.Of<ITaskRepository>(), Mock.Of<ITaskTemplateRepository>());
        var usersVm = new UsersViewModel(Mock.Of<IUserRepository>());
        var reports = new ReportsViewModel(
            Mock.Of<ITimeLogRepository>(), Mock.Of<ITimeLogService>(), Mock.Of<ISettingsRepository>(),
            Mock.Of<IClock>(), Mock.Of<IReportAggregator>());
        var settings = new SettingsViewModel(
            Mock.Of<IAppConfig>(), Mock.Of<ISettingsRepository>(), Mock.Of<ITaskTemplateRepository>(),
            Mock.Of<IDefaultTaskSyncService>());

        // Default conflict scan target: a path with no siblings so no spurious warning fires.
        _config.SetupGet(c => c.DbPath).Returns(
            dbPath ?? Path.Combine(Path.GetTempPath(), "mvm-test-" + Guid.NewGuid().ToString("N"), "timesheet.db"));

        return new MainViewModel(
            timesheet, requests, usersVm, reports, settings,
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
