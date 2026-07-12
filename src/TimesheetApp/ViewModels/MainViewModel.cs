using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
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
/// WPF-free: no dialog is opened here. An unmapped Windows account is auto-provisioned (see
/// <c>ResolveCurrentUserAsync</c>), and the VM persists the mapping via
/// <see cref="ICurrentUserService.SetWindowsUsernameAsync"/> so the DI <c>Func&lt;int&gt;</c>
/// (<c>currentUser.Current?.Id</c>) resolves for the child VMs.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentTeamService _currentTeam;
    private readonly IUserRepository _users;
    private readonly ITeamRepository _teams;
    private readonly IAppConfig _config;
    private readonly IMessenger _messenger;
    private readonly Func<string> _windowsUserName;
    // Re-entrancy guard: refreshing AvailableTeams/ActiveTeam from the service must not echo back
    // into the ActiveTeam setter (which would call SetActiveTeamAsync for a no-op change).
    private bool _suppressActiveTeamSet;

    public MainViewModel(
        TimesheetViewModel timesheet,
        BacklogsViewModel backlogs,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        DailyReportViewModel dailyReport,
        TaskListViewModel taskList,
        ICurrentUserService currentUser,
        ICurrentTeamService currentTeam,
        IUserRepository users,
        ITeamRepository teams,
        IAppConfig config)
        : this(timesheet, backlogs, usersVm, reports, settings, dailyReport, taskList, currentUser, currentTeam, users, teams, config,
               () => Environment.UserName)
    {
    }

    // Test seam: inject the Windows-username provider so NeedsSelection persistence is deterministic,
    // and an optional messenger so DataKind.Teams cross-tab sync can be isolated per test.
    internal MainViewModel(
        TimesheetViewModel timesheet,
        BacklogsViewModel backlogs,
        UsersViewModel usersVm,
        ReportsViewModel reports,
        SettingsViewModel settings,
        DailyReportViewModel dailyReport,
        TaskListViewModel taskList,
        ICurrentUserService currentUser,
        ICurrentTeamService currentTeam,
        IUserRepository users,
        ITeamRepository teams,
        IAppConfig config,
        Func<string> windowsUserName,
        IMessenger? messenger = null)
    {
        Timesheet = timesheet;
        Backlogs = backlogs;
        Users = usersVm;
        Reports = reports;
        Settings = settings;
        DailyReport = dailyReport;
        TaskList = taskList;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _users = users;
        _teams = teams;
        _config = config;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _windowsUserName = windowsUserName;

        // Live refresh of the team switcher: when teams/membership/active-team change anywhere
        // (Settings CRUD, SetActiveTeamAsync), rebuild the switcher list + selection. static lambda
        // + recipient arg keeps the weak ref.
        _messenger.Register<MainViewModel, DataChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind == DataKind.Teams) r.RefreshTeamsFromService();
        });
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

    // ===== Active-team switcher (TM-05, architecture §5a) =====

    // The current user's active team memberships. Source for the sidebar ComboBox.
    public ObservableCollection<Team> AvailableTeams { get; } = new();

    // The active team. The two-way bound ComboBox SelectedItem; setting it persists via the service.
    [ObservableProperty] private Team? _activeTeam;

    // Show the switcher whenever the user has any team, so the current team is always visible. With a
    // single team it's a read-only indicator (selecting the only team is a harmless no-op); it becomes a
    // real switcher once a 2nd team exists.
    public bool ShowTeamSwitcher => AvailableTeams.Count >= 1;

    // ComboBox SelectedItem change → persist the active team. Guarded against re-entrancy (the
    // service-driven refresh sets ActiveTeam directly), null, and same-id no-ops.
    partial void OnActiveTeamChanged(Team? value)
    {
        if (_suppressActiveTeamSet) return;
        if (value is null) return;
        if (value.Id == _currentTeam.ActiveTeamId) return;
        _ = SafeLoad(() => _currentTeam.SetActiveTeamAsync(value.Id));
    }

    /// <summary>
    /// Rebuild AvailableTeams + the selected ActiveTeam from <see cref="ICurrentTeamService"/>.
    /// Call after the service is initialized (W9 startup wiring invokes this right after
    /// <c>ICurrentTeamService.InitializeAsync</c>); also runs on every DataKind.Teams broadcast.
    /// On a team change this also reloads the active view's data so the working scope reflects the
    /// new team (mirrors the per-view reload OnActiveViewChanged uses).
    /// </summary>
    public void RefreshTeamsFromService()
    {
        _suppressActiveTeamSet = true;
        try
        {
            AvailableTeams.Clear();
            foreach (var t in _currentTeam.AvailableTeams) AvailableTeams.Add(t);
            ActiveTeam = AvailableTeams.FirstOrDefault(t => t.Id == _currentTeam.ActiveTeamId);
        }
        finally
        {
            _suppressActiveTeamSet = false;
        }

        OnPropertyChanged(nameof(ShowTeamSwitcher));

        // Reload the currently active view so its data reflects the (possibly) new active team.
        OnActiveViewChanged(ActiveView);
    }

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
    /// resolve the current user (auto-provisioning an unmapped Windows account), compute the
    /// conflict-copy warning (XC-08), then best-effort load each tab VM.
    /// </summary>
    public async Task InitializeAsync()
    {
        var user = await ResolveCurrentUserAsync();
        await InitializeActiveTeamAsync(user);
        DetectConflictCopies();
        await LoadTabsAsync();
    }

    /// <summary>
    /// P10 (TM-05/TM-09, architecture §6b): resolve the active-team context for the just-resolved
    /// user and populate the sidebar switcher. The startup bootstrap (App.OnStartup) already created
    /// the team(s); here we make sure the resolved user is a member of the bootstrap team (idempotent
    /// INSERT OR IGNORE — a no-op for an already-migrated user, the actual first-run join for a
    /// freshly auto-created one), then init the current-team service so AvailableTeams includes the
    /// just-joined team and refresh the switcher.
    /// <para>
    /// M8.2 (Wave 4): the bootstrap team used to be read from IAppConfig.ActiveTeamId. That is gone —
    /// the active team is now per-USER (Users.active_team_id), and a per-process config value cannot
    /// answer "which team should a brand-new user join". The bootstrap team is instead derived from
    /// the DB as the lowest-id team, which is the SAME rule TeamBootstrapService uses to detect an
    /// existing bootstrap. Bonus: the join target is now machine-independent — it no longer depends on
    /// whichever team the last user of this PC happened to switch to.
    /// </para>
    /// </summary>
    private async Task InitializeActiveTeamAsync(User? user)
    {
        if (user is null) return; // user cancelled selection — no team context to resolve

        // First-run join: bootstrap's own user sweep only saw the users that existed when it ran, so a
        // user auto-provisioned just now (ResolveCurrentUserAsync) has no membership yet. Idempotent,
        // so it is a no-op for everyone else. Guarded on "no team at all" so we never insert a bogus
        // membership before bootstrap has run.
        var bootstrapTeam = (await _teams.GetAllAsync()).OrderBy(t => t.Id).FirstOrDefault();
        if (bootstrapTeam is not null)
            await _teams.AddMemberAsync(user.Id, bootstrapTeam.Id);

        // Resolve AvailableTeams + active team for this user (now including the just-joined team),
        // then push the result into the switcher.
        await _currentTeam.InitializeAsync(user.Id);
        RefreshTeamsFromService();
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

    // Returns the resolved current user so the caller can join them to the active team (TM-09 first-run join).
    private async Task<User?> ResolveCurrentUserAsync()
    {
        var result = await _currentUser.ResolveAsync();
        if (result.Outcome == CurrentUserOutcome.Resolved && result.User is { } resolved)
        {
            CurrentUserName = resolved.Name;
            return resolved;
        }

        // Unmapped Windows account => auto-provision: create a user named after the Windows account, map
        // it, and open straight into a usable session — no manual "add user" step and no picker, whether
        // or not other users already exist (self-service onboarding). InitializeActiveTeamAsync then joins
        // the new user to the active team.
        var winName = _windowsUserName();
        var displayName = string.IsNullOrWhiteSpace(winName) ? "Me" : winName.Trim();
        var newId = await _users.InsertAsync(new User(0, displayName, null, true));
        await _currentUser.SetWindowsUsernameAsync(newId, winName);
        CurrentUserName = _currentUser.Current?.Name ?? displayName;
        return _currentUser.Current ?? new User(newId, displayName, null, true);
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
        catch (Exception ex)
        {
            // Best-effort: a tab failing to preload must not prevent the shell from showing — but don't
            // swallow it silently (that masked past startup bugs). Surface to the debug output.
            System.Diagnostics.Debug.WriteLine($"[LoadTabs] preload failed: {ex}");
        }
    }
}
