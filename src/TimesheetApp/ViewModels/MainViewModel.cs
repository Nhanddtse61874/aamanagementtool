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
        BacklogsViewModel backlogs,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        DailyReportViewModel dailyReport,
        TaskListViewModel taskList,
        ICurrentUserService currentUser,
        IUserRepository users,
        IAppConfig config)
        : this(timesheet, backlogs, usersVm, reports, settings, dailyReport, taskList, currentUser, users, config,
               () => Environment.UserName)
    {
    }

    // Test seam: inject the Windows-username provider so NeedsSelection persistence is deterministic.
    internal MainViewModel(
        TimesheetViewModel timesheet,
        BacklogsViewModel backlogs,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        DailyReportViewModel dailyReport,
        TaskListViewModel taskList,
        ICurrentUserService currentUser,
        IUserRepository users,
        IAppConfig config,
        Func<string> windowsUserName)
    {
        Timesheet = timesheet;
        Backlogs = backlogs;
        Users = usersVm;
        Reports = reports;
        Settings = settings;
        DailyReport = dailyReport;
        TaskList = taskList;
        _currentUser = currentUser;
        _users = users;
        _config = config;
        _windowsUserName = windowsUserName;
    }

    public TimesheetViewModel Timesheet { get; }
    public BacklogsViewModel Backlogs { get; }
    public UsersViewModel Users { get; }
    public ReportsViewModel Reports { get; }
    public SettingsViewModel Settings { get; }
    public DailyReportViewModel DailyReport { get; }
    public TaskListViewModel TaskList { get; }

    [ObservableProperty] private string _currentUserName = string.Empty;

    // First letter of the current user's name (uppercased) for the sidebar avatar chip.
    public string CurrentUserInitial =>
        string.IsNullOrWhiteSpace(CurrentUserName) ? "?" : CurrentUserName.Trim()[..1].ToUpperInvariant();

    partial void OnCurrentUserNameChanged(string value) => OnPropertyChanged(nameof(CurrentUserInitial));

    // Active sidebar destination (string key): timesheet (= "Log Work") | backlog | tasklist |
    // dailyreport | reports | users | settings. Drives both the nav highlight (RadioButton IsChecked
    // via StringMatchConverter) and content-panel visibility. Each is now a top-level item.
    [ObservableProperty] private string _activeView = "timesheet";

    // Switching destinations reloads the relevant view so it shows fresh data. Top-level keys route
    // either through the index-based ActivateTabAsync (timesheet=0, users=2, reports=3, settings=4) or
    // straight to a VM load (backlog, tasklist, dailyreport).
    partial void OnActiveViewChanged(string value)
    {
        switch (value)
        {
            case "dailyreport": _ = SafeLoad(() => DailyReport.LoadAsync()); return;
            case "backlog": _ = SafeLoad(() => Backlogs.LoadAsync()); return;
            case "tasklist": _ = SafeLoad(() => TaskList.LoadAsync()); return;
            case "reports": _ = ActivateTabAsync(3); return;
            case "timesheet": _ = ActivateTabAsync(0); return;
            case "users": _ = ActivateTabAsync(2); return;
            case "settings": _ = ActivateTabAsync(4); return;
        }
    }

    // XC-08: non-empty when OneDrive conflict-copy siblings of the DB exist; the View shows a banner.
    [ObservableProperty] private string _conflictWarning = string.Empty;

    // XC-09: non-empty when a bulk write left a lingering rollback journal (data may be at risk).
    // App wires the UiJournalWarningSink event to this (marshalled onto the UI thread); a dismiss
    // button clears it. The warning is still traced regardless (never swallowed).
    [ObservableProperty] private string _journalWarning = string.Empty;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void DismissJournalWarning() => JournalWarning = string.Empty;

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

    /// <summary>
    /// Reload a view's data so changes made elsewhere are reflected (e.g. a task created in Backlog
    /// appears in the Log Work grid). Called from <see cref="OnActiveViewChanged"/> for the views that
    /// reuse this index-based reload. Index map (unchanged): 0 Log Work (timesheet), 1 Backlog,
    /// 2 Users, 3 Reports, 4 Settings.
    /// </summary>
    public async Task ActivateTabAsync(int index)
    {
        switch (index)
        {
            case 0: await SafeLoad(() => Timesheet.LoadCommand.ExecuteAsync(null)); break;
            case 1: await SafeLoad(() => Backlogs.LoadAsync()); break;
            case 2: await SafeLoad(() => Users.LoadAsync()); break;
            case 3:
                // Default Reports view = whole team, current month (+ week for the stat cards/weekly grid).
                // LoadUsers resets SelectedTarget to "Cả team"; the month/week default to "today" (VM ctor).
                await SafeLoad(() => Reports.LoadUsersAsync());
                await SafeLoad(() => Reports.LoadBannerAsync());
                await SafeLoad(() => Reports.LoadMonthlyCommand.ExecuteAsync(null));
                await SafeLoad(() => Reports.LoadWeeklyCommand.ExecuteAsync(null));
                break;
            case 4: await SafeLoad(() => Settings.LoadAsync()); break;
        }
    }

    private async Task ResolveCurrentUserAsync(Func<IReadOnlyList<User>, User?> selectUser)
    {
        var result = await _currentUser.ResolveAsync();
        if (result.Outcome == CurrentUserOutcome.Resolved && result.User is { } resolved)
        {
            CurrentUserName = resolved.Name;
            return;
        }

        var active = await _users.GetActiveAsync();

        // Zero-config first run: a fresh DB has no users at all. Don't prompt — auto-create a user
        // named after the Windows account and map it, so the app opens straight to a usable timesheet.
        if (active.Count == 0)
        {
            var winName = _windowsUserName();
            var displayName = string.IsNullOrWhiteSpace(winName) ? "Me" : winName.Trim();
            var newId = await _users.InsertAsync(new User(0, displayName, null, true));
            await _currentUser.SetWindowsUsernameAsync(newId, winName);
            CurrentUserName = _currentUser.Current?.Name ?? displayName;
            return;
        }

        // NeedsSelection with existing users: present them to the View, persist the chosen mapping (XC-07).
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
            : "Detected OneDrive conflict copies of the database: "
              + string.Join(", ", copies.Select(Path.GetFileName));
    }

    // Best-effort initial load so each tab shows data on open. Failures here must not block the
    // window from appearing, so each load is isolated.
    private async Task LoadTabsAsync()
    {
        await SafeLoad(() => Timesheet.LoadCommand.ExecuteAsync(null));
        await SafeLoad(() => Backlogs.LoadAsync());
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
