using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

/// <summary>
/// Application shell VM (spec §5 MainViewModel row, §6 startup). Hosts the 5 tab VMs, shows the
/// current user in the top corner (XC-07) and the conflict-copy startup banner (XC-08).
/// <para>
/// WPF-free: the SelectUserDialog is NOT opened here. On <see cref="CurrentUserOutcome.NeedsSelection"/>
/// the View supplies a <c>Func&lt;IReadOnlyList&lt;User&gt;, User?&gt;</c> selector to
/// <see cref="InitializeAsync"/>; the VM persists the chosen mapping via
/// <see cref="ICurrentUserService.SetWindowsUsernameAsync"/> so the DI <c>Func&lt;int&gt;</c>
/// (<c>currentUser.Current?.Id</c>) resolves for the child VMs.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICurrentUserService _currentUser;
    private readonly IUserRepository _users;
    private readonly IAppConfig _config;
    private readonly Func<string> _windowsUserName;

    public MainViewModel(
        TimesheetViewModel timesheet,
        RequestsViewModel requests,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        ICurrentUserService currentUser,
        IUserRepository users,
        IAppConfig config)
        : this(timesheet, requests, usersVm, reports, settings, currentUser, users, config,
               () => Environment.UserName)
    {
    }

    // Test seam: inject the Windows-username provider so NeedsSelection persistence is deterministic.
    internal MainViewModel(
        TimesheetViewModel timesheet,
        RequestsViewModel requests,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        ICurrentUserService currentUser,
        IUserRepository users,
        IAppConfig config,
        Func<string> windowsUserName)
    {
        Timesheet = timesheet;
        Requests = requests;
        Users = usersVm;
        Reports = reports;
        Settings = settings;
        _currentUser = currentUser;
        _users = users;
        _config = config;
        _windowsUserName = windowsUserName;
    }

    public TimesheetViewModel Timesheet { get; }
    public RequestsViewModel Requests { get; }
    public UsersViewModel Users { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private string _currentUserName = string.Empty;

    // XC-08: non-empty when OneDrive conflict-copy siblings of the DB exist; the View shows a banner.
    [ObservableProperty] private string _conflictWarning = string.Empty;

    /// <summary>
    /// Startup orchestration (spec §6, runs AFTER <see cref="IDatabaseInitializer.InitializeAsync"/>):
    /// resolve the current user (prompting via <paramref name="selectUser"/> on NeedsSelection),
    /// compute the conflict-copy warning (XC-08), then best-effort load each tab VM.
    /// </summary>
    /// <param name="selectUser">
    /// View-supplied picker shown only on NeedsSelection. Returns the chosen user, or null if the
    /// user cancelled. Kept as a delegate so the VM never references <c>System.Windows.*</c>.
    /// </param>
    public async Task InitializeAsync(Func<IReadOnlyList<User>, User?> selectUser)
    {
        await ResolveCurrentUserAsync(selectUser);
        DetectConflictCopies();
        await LoadTabsAsync();
    }

    private async Task ResolveCurrentUserAsync(Func<IReadOnlyList<User>, User?> selectUser)
    {
        var result = await _currentUser.ResolveAsync();
        if (result.Outcome == CurrentUserOutcome.Resolved && result.User is { } resolved)
        {
            CurrentUserName = resolved.Name;
            return;
        }

        // NeedsSelection: present active users to the View, persist the chosen mapping (XC-07).
        var active = await _users.GetActiveAsync();
        var chosen = selectUser(active);
        if (chosen is null) return; // cancelled — Current stays null, child VMs see id 0

        await _currentUser.SetWindowsUsernameAsync(chosen.Id, _windowsUserName());
        CurrentUserName = _currentUser.Current?.Name ?? chosen.Name;
    }

    private void DetectConflictCopies()
    {
        var copies = SqliteMaintenance.FindConflictCopies(_config.DbPath);
        ConflictWarning = copies.Count == 0
            ? string.Empty
            : "Phát hiện bản sao xung đột (OneDrive) của cơ sở dữ liệu: "
              + string.Join(", ", copies.Select(Path.GetFileName));
    }

    // Best-effort initial load so each tab shows data on open. Failures here must not block the
    // window from appearing, so each load is isolated.
    private async Task LoadTabsAsync()
    {
        await SafeLoad(() => Timesheet.LoadCommand.ExecuteAsync(null));
        await SafeLoad(() => Requests.LoadAsync());
        await SafeLoad(() => Users.LoadAsync());
        await SafeLoad(() => Reports.LoadBannerAsync());
        await SafeLoad(() => Settings.LoadAsync());
    }

    private static async Task SafeLoad(Func<Task> load)
    {
        try
        {
            await load();
        }
        catch
        {
            // Best-effort: a tab failing to preload must not prevent the shell from showing.
        }
    }
}
