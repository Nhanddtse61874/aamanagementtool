using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IClock _clock;
    private readonly IReportAggregator _aggregator;

    public ReportsViewModel(
        ITimeLogRepository timeLogs,
        ITimeLogService timeLogService,
        ISettingsRepository settings,
        IClock clock,
        IReportAggregator aggregator)
    {
        _timeLogs = timeLogs;
        _timeLogService = timeLogService;
        _settings = settings;
        _clock = clock;
        _aggregator = aggregator;

        // default selection = the week / month containing "today"
        var today = _clock.Today;
        SelectedWeekMonday = MondayOf(today);
        SelectedMonth = new DateOnly(today.Year, today.Month, 1);
    }

    [ObservableProperty] private int _selectedUserId;
    [ObservableProperty] private DateOnly _selectedWeekMonday;
    [ObservableProperty] private DateOnly _selectedMonth; // first of month

    public ObservableCollection<WeeklyDayTotal> WeeklyRows { get; } = new();
    public ObservableCollection<MonthlyRequestTaskTotal> MonthlyRows { get; } = new();
    public ObservableCollection<ProjectNode> ProjectTree { get; } = new();
    public ObservableCollection<MissingLogWarning> MissingBanner { get; } = new();

    [ObservableProperty] private string _bannerText = string.Empty;

    internal static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // Monday-start, culture-independent (spec §7.2)

    [RelayCommand]
    public async Task LoadWeeklyAsync()
    {
        var monday = SelectedWeekMonday;
        var friday = monday.AddDays(4);
        var rows = await _timeLogs.GetReportRowsAsync(SelectedUserId, monday, friday);
        WeeklyRows.Clear();
        foreach (var r in _aggregator.WeeklyDayTotals(rows)) WeeklyRows.Add(r);
    }

    [RelayCommand]
    public async Task LoadMonthlyAsync()
    {
        var first = SelectedMonth;
        var last = first.AddMonths(1).AddDays(-1);
        var rows = await _timeLogs.GetReportRowsAsync(SelectedUserId, first, last);
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
