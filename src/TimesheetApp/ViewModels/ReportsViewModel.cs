using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public sealed partial class ReportsViewModel : ObservableObject
{
    public const string NDaysKey = "chua_log_n_days";
    public const int DefaultNDays = 3; // SET-02

    private readonly ITimeLogRepository _timeLogs;
    private readonly ITimeLogService _timeLogService;
    private readonly ISettingsRepository _settings;
    private readonly IUserRepository _users;
    private readonly IClock _clock;
    private readonly IReportAggregator _aggregator;
    private readonly IMessenger _messenger;
    private readonly IExportService? _export;   // EXP-01: wired to the Export to Excel button
    private readonly ICurrentTeamService? _currentTeam;   // v8 (P10): multi-team filter
    private readonly IHolidayRepository? _holidays;   // M8.2: a holiday is not a working day (DAYS LOGGED)

    public ReportsViewModel(
        ITimeLogRepository timeLogs,
        ITimeLogService timeLogService,
        ISettingsRepository settings,
        IUserRepository users,
        IClock clock,
        IReportAggregator aggregator,
        IMessenger? messenger = null,
        IExportService? export = null,
        ICurrentTeamService? currentTeam = null,
        IHolidayRepository? holidays = null)
    {
        _timeLogs = timeLogs;
        _timeLogService = timeLogService;
        _settings = settings;
        _users = users;
        _clock = clock;
        _aggregator = aggregator;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _export = export;
        _currentTeam = currentTeam;
        _holidays = holidays;

        // P10 (TM-07): the multi-team checkbox filter; reload both grids on a selection/active-team change.
        if (_currentTeam is not null)
        {
            TeamFilter = new TeamFilterViewModel(_currentTeam);
            TeamFilter.SelectionChanged += (_, _) => { if (_autoLoad) _ = RefreshReportsAsync(); };
        }

        // default selection = the week / month containing "today"
        var today = _clock.Today;
        SelectedWeekMonday = DateHelpers.MondayOf(today);
        SelectedMonth = new DateOnly(today.Year, today.Month, 1);

        // default report target = whole team until users are loaded
        SelectedTarget = TeamTarget;
        Targets.Add(TeamTarget);

        // Live cross-tab sync: refresh the "chưa log" banner when logs or users change elsewhere;
        // also rebuild the report-target dropdown when the user list changes (Users tab add/remove).
        _messenger.Register<ReportsViewModel, DataChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind is DataKind.Logs or DataKind.Users)
                _ = r.LoadBannerAsync();
            if (m.Kind is DataKind.Users)
                _ = r.LoadUsersAsync();
        });
    }

    // UserId == 0 means the whole team (all users combined).
    public sealed record ReportTarget(int UserId, string Display);

    private static readonly ReportTarget TeamTarget = new(0, "Whole team (all)");

    public ObservableCollection<ReportTarget> Targets { get; } = new();

    // P10: the shared multi-team filter (null when no current-team service is wired — legacy ctor / tests).
    public TeamFilterViewModel? TeamFilter { get; }

    [ObservableProperty] private ReportTarget? _selectedTarget;
    [ObservableProperty] private DateOnly _selectedWeekMonday;
    [ObservableProperty] private DateOnly _selectedMonth; // first of month

    // Project filter ("All" = no filter). Reused for the grids and the Excel export.
    public IReadOnlyList<string> Projects { get; } =
        new[] { "All" }.Concat(BacklogProjects.All).ToList();
    [ObservableProperty] private string _selectedProject = "All";
    private string? ProjectFilter =>
        string.IsNullOrEmpty(SelectedProject) || SelectedProject == "All" ? null : SelectedProject;

    // Reports auto-load when a filter changes (no more Weekly/Monthly buttons). Suppressed until the
    // first explicit load (ActivateTab) has run so the ctor/LoadUsers don't fire premature reloads.
    private bool _autoLoad;

    private async Task RefreshReportsAsync()
    {
        await LoadWeeklyAsync();
        await LoadMonthlyAsync();
    }

    partial void OnSelectedTargetChanged(ReportTarget? value) { if (_autoLoad) _ = RefreshReportsAsync(); }
    partial void OnSelectedWeekMondayChanged(DateOnly value) { if (_autoLoad) _ = LoadWeeklyAsync(); }
    partial void OnSelectedMonthChanged(DateOnly value) { if (_autoLoad) _ = LoadMonthlyAsync(); }
    partial void OnSelectedProjectChanged(string value) { if (_autoLoad) _ = RefreshReportsAsync(); }

    // Drill-down tree expand/collapse-all toggle (bound to every TreeViewItem.IsExpanded).
    [ObservableProperty] private bool _drillExpanded;
    public string DrillToggleText => DrillExpanded ? "Collapse all" : "Expand all";
    partial void OnDrillExpandedChanged(bool value) => OnPropertyChanged(nameof(DrillToggleText));

    [RelayCommand]
    private void ToggleDrillExpand() => DrillExpanded = !DrillExpanded;

    public ObservableCollection<WeeklyDayTotal> WeeklyRows { get; } = new();
    public ObservableCollection<WeeklyDetailRow> WeeklyDetailRows { get; } = new();
    public ObservableCollection<MonthlyBacklogTaskTotal> MonthlyRows { get; } = new();
    public ObservableCollection<TeamNode> ProjectTree { get; } = new();
    public ObservableCollection<MissingLogWarning> MissingBanner { get; } = new();

    [ObservableProperty] private string _bannerText = string.Empty;

    // Weekly summary stat cards (computed from WeeklyRows after LoadWeekly).
    [ObservableProperty] private string _weekTotalText = "0.0h";
    [ObservableProperty] private string _avgPerDayText = "0.0h";
    [ObservableProperty] private string _daysLoggedText = "0 / 5";

    // M8.2: the denominator used to be WeeklyRows.Count — but WeeklyRows only holds days that HAVE logs,
    // so it moved with the numerator and the stat could only ever read N/N. The working-day count is
    // business arithmetic, so ReportAggregator owns it now; this method only formats the result.
    private void RecomputeWeeklyStats(IReadOnlySet<DateOnly> holidays)
    {
        var total = WeeklyRows.Sum(r => r.TotalHours);
        var stat = _aggregator.DaysLogged(WeeklyRows, SelectedWeekMonday, holidays);
        WeekTotalText = $"{total:N1}h";
        AvgPerDayText = $"{(stat.Logged == 0 ? 0m : total / stat.Logged):N1}h";
        DaysLoggedText = $"{stat.Logged} / {stat.WorkingDays}";
    }

    /// <summary>
    /// Rebuild the report-target dropdown: a leading "whole team" option followed by one option per
    /// active user (by name). Preserves the current selection when it still exists, else defaults to team.
    /// </summary>
    public async Task LoadUsersAsync()
    {
        var active = await _users.GetActiveAsync();
        var previousId = SelectedTarget?.UserId ?? 0;

        Targets.Clear();
        Targets.Add(TeamTarget);
        foreach (var u in active) Targets.Add(new ReportTarget(u.Id, u.Name));

        SelectedTarget = Targets.FirstOrDefault(t => t.UserId == previousId) ?? TeamTarget;
        _autoLoad = true; // from now on, changing any filter reloads the grids automatically
    }

    // Report rows for the selected target over [from, to]: whole team uses the all-users export query,
    // a single user uses the per-user report query.
    private async Task<IReadOnlyList<TimeLogReportRow>> GetRowsForTargetAsync(DateOnly from, DateOnly to)
    {
        // P10 (TM-07): scope to the checked teams (null = all teams = legacy/no-filter behavior).
        var teamIds = TeamFilter?.CheckedTeamIds;
        var target = SelectedTarget;
        var rows = target is null || target.UserId == 0
            ? await _timeLogs.GetExportRowsAsync(from, to, null, teamIds)
            : await _timeLogs.GetReportRowsAsync(target.UserId, from, to, teamIds);
        var project = ProjectFilter;
        return project is null ? rows : rows.Where(r => r.Project == project).ToList();
    }

    /// EXP-01: build the .xlsx bytes for the current selection (month + target + project). The View
    /// owns the SaveFileDialog and writes the bytes; returns null if no export service is wired.
    public Task<byte[]>? BuildExcelExportAsync()
    {
        if (_export is null) return null;
        var filter = new ExportFilter(
            SelectedTarget is { UserId: > 0 } t ? t.UserId : (int?)null,
            SelectedMonth.Year, SelectedMonth.Month, ProjectFilter,
            TeamFilter?.CheckedTeamIds);   // P10 (TM-08): scope the export to the checked teams
        return _export.ExportExcelAsync(filter);
    }

    /// Suggested file name for the export, e.g. "Worklog-2026-06-Nhan.xlsx".
    public string SuggestedExportFileName()
    {
        var who = SelectedTarget is { UserId: > 0 } t ? t.Display : "team";
        var safe = string.Concat(who.Split(System.IO.Path.GetInvalidFileNameChars()));
        return $"Worklog-{SelectedMonth:yyyy-MM}-{safe}.xlsx";
    }

    [RelayCommand]
    public async Task LoadWeeklyAsync()
    {
        var monday = SelectedWeekMonday;
        var friday = monday.AddDays(4);
        var rows = await GetRowsForTargetAsync(monday, friday);
        WeeklyRows.Clear();
        foreach (var r in _aggregator.WeeklyDayTotals(rows)) WeeklyRows.Add(r);
        WeeklyDetailRows.Clear();
        foreach (var r in _aggregator.WeeklyDetailRows(rows)) WeeklyDetailRows.Add(r);

        // HOL-02: a holiday is not a working day, so it must not inflate the DAYS LOGGED denominator.
        var holidays = _holidays is null
            ? new HashSet<DateOnly>()
            : (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();
        RecomputeWeeklyStats(holidays);
    }

    [RelayCommand]
    public async Task LoadMonthlyAsync()
    {
        var first = SelectedMonth;
        var last = first.AddMonths(1).AddDays(-1);
        var rows = await GetRowsForTargetAsync(first, last);
        MonthlyRows.Clear();
        foreach (var r in _aggregator.MonthlyBacklogTaskTotals(rows)) MonthlyRows.Add(r);
        ProjectTree.Clear();
        foreach (var p in _aggregator.BuildProjectTree(rows)) ProjectTree.Add(p);
    }

    [RelayCommand]
    public async Task LoadBannerAsync()
    {
        var n = await GetConfiguredNAsync();
        var missing = await _timeLogService.GetUsersMissingLogsAsync(n);
        MissingBanner.Clear();
        foreach (var u in missing) MissingBanner.Add(new MissingLogWarning(u.Name));
        BannerText = MissingBanner.Count == 0
            ? string.Empty
            : string.Join("; ", MissingBanner.Select(w => $"{w.UserName} has not logged in {n} days"));
    }

    private async Task<int> GetConfiguredNAsync()
    {
        var raw = await _settings.GetAsync(NDaysKey);
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultNDays; // SET-02 default 3
    }
}
