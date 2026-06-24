using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
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
    private readonly IUserRepository? _users;        // v2: Entry user filter (Cả team / other user)
    private readonly IRequestRepository? _requests;  // v2: move-ticket-to-month
    private readonly ICurrentUserService? _currentUser; // v2: audit changed-by for move-month
    private bool _suppressTotals;

    public TimesheetViewModel(
        ITimeLogService timeLogs, ITaskRepository tasks, ISmartInputService smartInput, IClock clock,
        Func<int> currentUserId, IMessenger? messenger = null,
        IUserRepository? users = null, IRequestRepository? requests = null,
        ICurrentUserService? currentUser = null)
    {
        _timeLogs = timeLogs;
        _tasks = tasks;
        _clock = clock;
        _currentUserId = currentUserId;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _users = users;
        _requests = requests;
        _currentUser = currentUser;

        // Smart-fill targets whichever user the Entry filter is viewing (defaults to the login user).
        // It looks up a request by code + lists its tasks, so it needs the request/task repositories.
        SmartInput = new SmartInputPanelVm(timeLogs, requests, tasks, () => EffectiveUserId);
        SmartInput.Applied += async () => await ReloadAsync();

        // Assign the backing fields directly so the change handlers (which reload) do NOT fire
        // during construction — the first load is driven by LoadCommand.
        var today = _clock.Today;
        _selectedMonth = new DateOnly(today.Year, today.Month, 1);
        _filterMonthNumber = today.Month;
        _filterYear = today.Year;

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

    // ---- v2: Entry user filter + month filter ----
    // IsTeam=true => read-only aggregate across all users; else a specific user (editable).
    public sealed record EntryTarget(int UserId, string Display, bool IsTeam);

    public ObservableCollection<EntryTarget> Targets { get; } = new();

    [ObservableProperty] private EntryTarget? _selectedTarget;
    [ObservableProperty] private DateOnly _selectedMonth; // first-of-month; filters which tickets show

    // Month filter is picked as month + year combos (not a full date picker).
    [ObservableProperty] private int _filterMonthNumber;
    [ObservableProperty] private int _filterYear;
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 2, 6).ToList();

    partial void OnFilterMonthNumberChanged(int value) => SelectedMonth = new DateOnly(FilterYear, value, 1);
    partial void OnFilterYearChanged(int value) => SelectedMonth = new DateOnly(value, FilterMonthNumber, 1);

    // The user whose hours are loaded/edited (login user unless another is picked; 0 for team view).
    private int EffectiveUserId =>
        SelectedTarget is { IsTeam: false } t ? t.UserId : (SelectedTarget is null ? _currentUserId() : 0);

    public bool IsTeamView => SelectedTarget is { IsTeam: true };
    public bool IsReadOnly => IsTeamView;               // team aggregate cannot be edited
    public bool CanEdit => !IsReadOnly;

    partial void OnSelectedTargetChanged(EntryTarget? value)
    {
        OnPropertyChanged(nameof(IsTeamView));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        _ = ReloadAsync();
    }

    partial void OnSelectedMonthChanged(DateOnly value)
    {
        OnPropertyChanged(nameof(SelectedMonthText));
        _ = ReloadAsync();
    }

    public string SelectedMonthText => SelectedMonth.ToString("MM/yyyy");

    // Build the Entry target list: "Cả team" + each active user. Default = the login user.
    private async Task LoadTargetsAsync()
    {
        if (_users is null) return;
        var active = await _users.GetActiveAsync();
        var meId = _currentUserId();

        Targets.Clear();
        Targets.Add(new EntryTarget(0, "Whole team (read-only)", IsTeam: true));
        foreach (var u in active) Targets.Add(new EntryTarget(u.Id, u.Name, IsTeam: false));

        SelectedTarget ??= Targets.FirstOrDefault(t => !t.IsTeam && t.UserId == meId)
                           ?? Targets.FirstOrDefault(t => !t.IsTeam);
    }

    public ObservableCollection<RequestGroupVm> Groups { get; } = new();

    /// All task rows across every group — used for footer totals + Save iteration.
    private IEnumerable<TimesheetRowVm> AllRows => Groups.SelectMany(g => g.Tasks);

    [ObservableProperty] private decimal _monTotal;
    [ObservableProperty] private decimal _tueTotal;
    [ObservableProperty] private decimal _wedTotal;
    [ObservableProperty] private decimal _thuTotal;
    [ObservableProperty] private decimal _friTotal;

    /// Grand total across the visible week — shown in the "Week total" header chip.
    public decimal WeekTotal => MonTotal + TueTotal + WedTotal + ThuTotal + FriTotal;

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
    private async Task LoadAsync()
    {
        await LoadTargetsAsync();
        await ReloadAsync();
    }

    // Quick collapse/expand of ALL request groups at once (Entry header button).
    [ObservableProperty] private bool _allCollapsed;

    public string CollapseToggleText => AllCollapsed ? "Expand all" : "Collapse all";
    partial void OnAllCollapsedChanged(bool value) => OnPropertyChanged(nameof(CollapseToggleText));

    [RelayCommand]
    private void ToggleCollapseAll()
    {
        AllCollapsed = !AllCollapsed;
        foreach (var g in Groups) g.IsExpanded = !AllCollapsed;
    }

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
        // Team view = read-only aggregate across all users; otherwise the selected/login user's week.
        var grouped = IsTeamView
            ? await _timeLogs.GetWeekGroupedAllUsersAsync(CurrentWeek)
            : await _timeLogs.GetWeekGroupedAsync(EffectiveUserId, CurrentWeek);

        // Month filter: only show tickets assigned to the selected month; tickets with no month
        // (DEFAULT + not-yet-assigned legacy tickets) stay visible regardless.
        var monthKey = SelectedMonth.ToString("yyyy-MM");
        grouped = grouped
            .Where(g => string.IsNullOrEmpty(g.PeriodMonth) || g.PeriodMonth == monthKey)
            .ToList();

        _suppressTotals = true;

        // Preserve each group's expand/collapse state across reloads, keyed by RequestId.
        var expandedById = Groups.ToDictionary(g => g.RequestId, g => g.IsExpanded);
        foreach (var r in AllRows) r.DayChanged -= OnRowDayChanged;
        Groups.Clear();

        foreach (var grp in grouped)
        {
            var groupVm = new RequestGroupVm(
                grp.RequestId, grp.RequestCode, grp.Project, _tasks, OnTaskAddedAsync, grp.PeriodMonth, grp.Status);
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

        // Keep groups collapsed across reloads while "Collapse all" is active.
        if (AllCollapsed) foreach (var g in Groups) g.IsExpanded = false;

        OnPropertyChanged(nameof(MonHeader));
        OnPropertyChanged(nameof(TueHeader));
        OnPropertyChanged(nameof(WedHeader));
        OnPropertyChanged(nameof(ThuHeader));
        OnPropertyChanged(nameof(FriHeader));
        RecomputeTotals();
    }

    private void OnRowDayChanged(TimesheetRowVm row, DayColumn col)
    {
        if (_suppressTotals) return;
        RecomputeTotals();
        _lastAutoSave = AutoSaveCellAsync(row, col); // persist just this cell (no Save button)
    }

    // ---- Auto-save (replaces the Save button) ----
    // Status text shown where the Save button used to be: "Saving…" / "✓ Saved" / a warning.
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private bool _saveStatusIsError;

    private Task? _lastAutoSave;
    // Test hook: await the most recent auto-save so assertions don't race the fire-from-setter task.
    internal Task LastAutoSave => _lastAutoSave ?? Task.CompletedTask;

    private void SetStatus(string text, bool isError)
    {
        SaveStatus = text;
        SaveStatusIsError = isError;
    }

    /// Persist a single cell as soon as it commits. A red (invalid) cell is left on screen but never
    /// written; the service also rejects a cell that would push the day over 8h (revert on reload).
    private async Task AutoSaveCellAsync(TimesheetRowVm row, DayColumn col)
    {
        if (IsTeamView) return;                      // team aggregate is read-only
        if (row.HasErrorFor(col))
        {
            SetStatus("Not saved — fix the highlighted cell", isError: true);
            return;
        }

        SetStatus("Saving…", isError: false);
        var result = await SaveCellAsync(row, col);
        SetStatus(result.Ok ? "✓ Saved" : $"⚠ {result.Error}", isError: !result.Ok);
    }

    private void RecomputeTotals()
    {
        var rows = AllRows.ToList();
        MonTotal = rows.Sum(r => r.Mon ?? 0);
        TueTotal = rows.Sum(r => r.Tue ?? 0);
        WedTotal = rows.Sum(r => r.Wed ?? 0);
        ThuTotal = rows.Sum(r => r.Thu ?? 0);
        FriTotal = rows.Sum(r => r.Fri ?? 0);
        OnPropertyChanged(nameof(WeekTotal));
        foreach (var g in Groups) g.RefreshTotal(); // live per-request header totals
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

    private bool CanSave() => !AnyDayOverEight() && !IsTeamView; // team aggregate is read-only

    /// Persist one cell for the effective user: value -> upsert on natural key (TS-07); empty -> delete.
    /// Returns the service's SaveResult (Ok=false when the day would exceed 8h); clears are always Ok.
    public async Task<SaveResult> SaveCellAsync(TimesheetRowVm row, DayColumn col)
    {
        if (IsTeamView) return new SaveResult(true, null);
        var date = CurrentWeek.AddDays((int)col);
        var value = col switch
        {
            DayColumn.Mon => row.Mon,
            DayColumn.Tue => row.Tue,
            DayColumn.Wed => row.Wed,
            DayColumn.Thu => row.Thu,
            _ => row.Fri
        };
        if (value is { } v) return await _timeLogs.SaveCellAsync(EffectiveUserId, row.TaskId, date, v);
        await _timeLogs.ClearCellAsync(EffectiveUserId, row.TaskId, date);
        return new SaveResult(true, null);
    }

    /// Move a ticket to the NEXT month (its period_month bumps by one). Audited as the current user,
    /// then the grid reloads so the ticket leaves the current month's view (TS / v2).
    [RelayCommand]
    private async Task MoveMonthAsync(int requestId)
    {
        if (_requests is null) return;
        var req = await _requests.GetByIdAsync(requestId);
        if (req is null) return;

        // Base month = the ticket's current period (or the filter month if unassigned), then +1.
        var baseMonth = ParseMonth(req.PeriodMonth) ?? SelectedMonth;
        var next = baseMonth.AddMonths(1).ToString("yyyy-MM");

        await _requests.UpdateAsync(req with { PeriodMonth = next },
            _currentUser?.Current?.Id, _currentUser?.Current?.Name);
        await ReloadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Requests));
    }

    private static DateOnly? ParseMonth(string? yyyymm) =>
        DateOnly.TryParseExact(yyyymm + "-01", "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    // Test-only hook to exercise the Applied -> reload wiring without WPF dispatcher.
    internal void RaiseSmartInputAppliedForTest() => _ = ReloadAsync();
}
