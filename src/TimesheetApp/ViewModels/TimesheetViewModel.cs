using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public enum DayColumn { Mon, Tue, Wed, Thu, Fri }

/// Timesheet tab VM (TS-01..07 + hosts SI-05/06 panel).
/// Owns week navigation, row shaping, per-column footer totals, Save gating, and per-cell persistence.
public sealed partial class TimesheetViewModel : ObservableObject
{
    private readonly ITimeLogService _timeLogs;
    private readonly ITaskRepository _tasks;
    private readonly IClock _clock;
    private readonly Func<int> _currentUserId;
    private readonly IMessenger _messenger;
    private bool _suppressTotals;

    public TimesheetViewModel(
        ITimeLogService timeLogs, ITaskRepository tasks, ISmartInputService smartInput, IClock clock,
        Func<int> currentUserId, IMessenger? messenger = null)
    {
        _timeLogs = timeLogs;
        _tasks = tasks;
        _clock = clock;
        _currentUserId = currentUserId;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        SmartInput = new SmartInputPanelVm(smartInput, timeLogs, currentUserId);
        SmartInput.Applied += async () => await ReloadAsync();

        // Live cross-tab sync: reload the grid when tasks/templates/default-tasks change elsewhere
        // (e.g. a task created in the Requests tab). static lambda + recipient arg keeps the weak ref.
        _messenger.Register<TimesheetViewModel, DataChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind is DataKind.Tasks or DataKind.Templates or DataKind.DefaultTasks or DataKind.Requests)
                _ = r.ReloadAsync();
        });

        CurrentWeek = MondayOf(_clock.Today);
    }

    public SmartInputPanelVm SmartInput { get; }

    [ObservableProperty] private DateOnly _currentWeek;

    public ObservableCollection<RequestGroupVm> Groups { get; } = new();

    /// All task rows across every group — used for footer totals + Save iteration.
    private IEnumerable<TimesheetRowVm> AllRows => Groups.SelectMany(g => g.Tasks);

    [ObservableProperty] private decimal _monTotal;
    [ObservableProperty] private decimal _tueTotal;
    [ObservableProperty] private decimal _wedTotal;
    [ObservableProperty] private decimal _thuTotal;
    [ObservableProperty] private decimal _friTotal;

    public string MonHeader => Header(0);
    public string TueHeader => Header(1);
    public string WedHeader => Header(2);
    public string ThuHeader => Header(3);
    public string FriHeader => Header(4);

    private string Header(int offset)
    {
        var d = CurrentWeek.AddDays(offset);
        var dow = d.DayOfWeek.ToString()[..3]; // Mon/Tue/...
        return $"{dow} {d:dd/MM}";
    }

    /// Hard-coded Monday week start (NOT culture-derived) — spec §7.2.
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    [RelayCommand]
    private Task LoadAsync() => ReloadAsync();

    [RelayCommand]
    private async Task NextWeekAsync()
    {
        CurrentWeek = CurrentWeek.AddDays(7);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task PreviousWeekAsync()
    {
        CurrentWeek = CurrentWeek.AddDays(-7);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var grouped = await _timeLogs.GetWeekGroupedAsync(_currentUserId(), CurrentWeek);

        _suppressTotals = true;

        // Preserve each group's expand/collapse state across reloads, keyed by RequestId.
        var expandedById = Groups.ToDictionary(g => g.RequestId, g => g.IsExpanded);
        foreach (var r in AllRows) r.DayChanged -= OnRowDayChanged;
        Groups.Clear();

        foreach (var grp in grouped)
        {
            var groupVm = new RequestGroupVm(
                grp.RequestId, grp.RequestCode, grp.Project, _tasks, OnTaskAddedAsync);
            if (expandedById.TryGetValue(grp.RequestId, out var wasExpanded))
                groupVm.IsExpanded = wasExpanded;

            foreach (var wr in grp.Tasks)
            {
                var row = new TimesheetRowVm
                {
                    TaskId = wr.TaskId,
                    RequestCode = wr.RequestCode,
                    Project = grp.Project,
                    TaskName = wr.TaskName,
                    Mon = wr.Mon,
                    Tue = wr.Tue,
                    Wed = wr.Wed,
                    Thu = wr.Thu,
                    Fri = wr.Fri
                };
                row.DayChanged += OnRowDayChanged;
                groupVm.Tasks.Add(row);
            }
            Groups.Add(groupVm);
        }
        _suppressTotals = false;

        OnPropertyChanged(nameof(MonHeader));
        OnPropertyChanged(nameof(TueHeader));
        OnPropertyChanged(nameof(WedHeader));
        OnPropertyChanged(nameof(ThuHeader));
        OnPropertyChanged(nameof(FriHeader));
        RecomputeTotals();
    }

    private void OnRowDayChanged()
    {
        if (_suppressTotals) return;
        RecomputeTotals();
    }

    private void RecomputeTotals()
    {
        var rows = AllRows.ToList();
        MonTotal = rows.Sum(r => r.Mon ?? 0);
        TueTotal = rows.Sum(r => r.Tue ?? 0);
        WedTotal = rows.Sum(r => r.Wed ?? 0);
        ThuTotal = rows.Sum(r => r.Thu ?? 0);
        FriTotal = rows.Sum(r => r.Fri ?? 0);
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// Inline add-task callback handed to each RequestGroupVm: after the Task is inserted, reload the
    /// grid (so the new empty row appears) and broadcast so other tabs refresh too.
    private async Task OnTaskAddedAsync()
    {
        await ReloadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    private bool AnyDayOverEight() =>
        new[] { MonTotal, TueTotal, WedTotal, ThuTotal, FriTotal }.Any(t => t > 8m);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        foreach (var row in AllRows.ToList())
            foreach (DayColumn col in Enum.GetValues<DayColumn>())
                await SaveCellAsync(row, col);
        await ReloadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Logs)); // live-sync: Reports refresh
    }

    private bool CanSave() => !AnyDayOverEight();

    /// Persist one cell: value -> upsert on natural key (TS-07); empty -> delete (TS-03).
    public async Task SaveCellAsync(TimesheetRowVm row, DayColumn col)
    {
        var date = CurrentWeek.AddDays((int)col);
        var value = col switch
        {
            DayColumn.Mon => row.Mon,
            DayColumn.Tue => row.Tue,
            DayColumn.Wed => row.Wed,
            DayColumn.Thu => row.Thu,
            _ => row.Fri
        };
        if (value is { } v) await _timeLogs.SaveCellAsync(_currentUserId(), row.TaskId, date, v);
        else await _timeLogs.ClearCellAsync(_currentUserId(), row.TaskId, date);
    }

    // Test-only hook to exercise the Applied -> reload wiring without WPF dispatcher.
    internal void RaiseSmartInputAppliedForTest() => _ = ReloadAsync();
}
