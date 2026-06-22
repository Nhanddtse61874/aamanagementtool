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

    public ReportsViewModel(
        ITimeLogRepository timeLogs,
        ITimeLogService timeLogService,
        ISettingsRepository settings,
        IUserRepository users,
        IClock clock,
        IReportAggregator aggregator,
        IMessenger? messenger = null)
    {
        _timeLogs = timeLogs;
        _timeLogService = timeLogService;
        _settings = settings;
        _users = users;
        _clock = clock;
        _aggregator = aggregator;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        // default selection = the week / month containing "today"
        var today = _clock.Today;
        SelectedWeekMonday = MondayOf(today);
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

    private static readonly ReportTarget TeamTarget = new(0, "Cả team (tất cả)");

    public ObservableCollection<ReportTarget> Targets { get; } = new();

    [ObservableProperty] private ReportTarget? _selectedTarget;
    [ObservableProperty] private DateOnly _selectedWeekMonday;
    [ObservableProperty] private DateOnly _selectedMonth; // first of month

    public ObservableCollection<WeeklyDayTotal> WeeklyRows { get; } = new();
    public ObservableCollection<MonthlyRequestTaskTotal> MonthlyRows { get; } = new();
    public ObservableCollection<ProjectNode> ProjectTree { get; } = new();
    public ObservableCollection<MissingLogWarning> MissingBanner { get; } = new();

    [ObservableProperty] private string _bannerText = string.Empty;

    internal static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // Monday-start, culture-independent (spec §7.2)

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
    }

    // Report rows for the selected target over [from, to]: whole team uses the all-users export query,
    // a single user uses the per-user report query.
    private Task<IReadOnlyList<TimeLogReportRow>> GetRowsForTargetAsync(DateOnly from, DateOnly to)
    {
        var target = SelectedTarget;
        return target is null || target.UserId == 0
            ? _timeLogs.GetExportRowsAsync(from, to, null)
            : _timeLogs.GetReportRowsAsync(target.UserId, from, to);
    }

    [RelayCommand]
    public async Task LoadWeeklyAsync()
    {
        var monday = SelectedWeekMonday;
        var friday = monday.AddDays(4);
        var rows = await GetRowsForTargetAsync(monday, friday);
        WeeklyRows.Clear();
        foreach (var r in _aggregator.WeeklyDayTotals(rows)) WeeklyRows.Add(r);
    }

    [RelayCommand]
    public async Task LoadMonthlyAsync()
    {
        var first = SelectedMonth;
        var last = first.AddMonths(1).AddDays(-1);
        var rows = await GetRowsForTargetAsync(first, last);
        MonthlyRows.Clear();
        foreach (var r in _aggregator.MonthlyRequestTaskTotals(rows)) MonthlyRows.Add(r);
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
            : string.Join("; ", MissingBanner.Select(w => $"{w.UserName} chưa log trong {n} ngày"));
    }

    private async Task<int> GetConfiguredNAsync()
    {
        var raw = await _settings.GetAsync(NDaysKey);
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultNDays; // SET-02 default 3
    }
}
