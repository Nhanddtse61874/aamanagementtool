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
    private readonly IBacklogRepository? _backlogs;  // v2: move-ticket-to-month
    private readonly ICurrentUserService? _currentUser; // v2: audit changed-by for move-month
    private readonly ISettingsRepository? _settings; // persists the collapse-all preference across restarts
    private readonly IHolidayRepository? _holidays;  // HOL-02: holiday day-columns are non-working (ISSUE 5)
    private readonly ICurrentTeamService? _currentTeam; // TM-06: scope Smart Fill search to the active team
    private bool _suppressTotals;

    // The holidays inside the currently shown Mon–Fri week (loaded each ReloadAsync). Drives the per-day
    // read-only + visual flags so a holiday column behaves like a weekend in the entry grid (ISSUE 5).
    private readonly HashSet<DateOnly> _weekHolidays = new();

    // Settings key for the Entry "Collapse all" toggle (remembered between app sessions).
    private const string CollapseAllKey = "entry.collapseAll";

    public TimesheetViewModel(
        ITimeLogService timeLogs, ITaskRepository tasks, ISmartInputService smartInput, IClock clock,
        Func<int> currentUserId, IMessenger? messenger = null,
        IUserRepository? users = null, IBacklogRepository? backlogs = null,
        ICurrentUserService? currentUser = null, ISettingsRepository? settings = null,
        IHolidayRepository? holidays = null, ICurrentTeamService? currentTeam = null)
    {
        _timeLogs = timeLogs;
        _tasks = tasks;
        _clock = clock;
        _currentUserId = currentUserId;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _users = users;
        _backlogs = backlogs;
        _currentUser = currentUser;
        _settings = settings;
        _holidays = holidays;
        _currentTeam = currentTeam;

        // Smart-fill targets whichever user the Entry filter is viewing (defaults to the login user).
        // It looks up a backlog by code + lists its tasks, so it needs the backlog/task repositories.
        // TM-06: pass the active-team id so the search is scoped to the current team (0 => matches none).
        SmartInput = new SmartInputPanelVm(
            timeLogs, backlogs, tasks, () => EffectiveUserId, () => _currentTeam?.ActiveTeamId ?? 0);
        SmartInput.Applied += async () =>
        {
            await ReloadAsync();
            _messenger.Send(new DataChangedMessage(DataKind.Logs)); // live-sync: Reports/Task List refresh
        };

        // Assign the backing fields directly so the change handlers (which reload) do NOT fire
        // during construction — the first load is driven by LoadCommand.
        var today = _clock.Today;
        _selectedMonth = new DateOnly(today.Year, today.Month, 1);
        _filterMonthNumber = today.Month;
        _filterYear = today.Year;

        // Live cross-tab sync: reload the grid when tasks/templates/default-tasks change elsewhere
        // (e.g. a task created in the Backlog tab). static lambda + recipient arg keeps the weak ref.
        _messenger.Register<TimesheetViewModel, DataChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind is DataKind.Tasks or DataKind.Templates or DataKind.DefaultTasks
                or DataKind.Backlogs or DataKind.Holidays)
                _ = r.ReloadAsync();
        });

        CurrentWeek = DateHelpers.MondayOf(_clock.Today);
    }

    public SmartInputPanelVm SmartInput { get; }

    [ObservableProperty] private DateOnly _currentWeek;

    // Jump-to-week: a DatePicker bound here lets the user snap the grid to ANY week (not just Prev/Next).
    // Picking a date jumps to that date's Monday; navigating Prev/Next keeps the picker in sync.
    [ObservableProperty] private DateTime? _jumpDate;

    partial void OnCurrentWeekChanged(DateOnly value) => JumpDate = value.ToDateTime(TimeOnly.MinValue);

    partial void OnJumpDateChanged(DateTime? value)
    {
        if (value is null) return;
        var monday = DateHelpers.MondayOf(DateOnly.FromDateTime(value.Value));
        if (monday == CurrentWeek) return; // already on that week (also breaks the sync feedback loop)
        CurrentWeek = monday;
        _ = ReloadAsync();
    }

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

    // ISSUE 5 (HOL-02): per-day-column holiday flags for the visible week + the combined read-only flag
    // each day cell binds to (team-view read-only OR the day is a holiday). A holiday column is shown
    // distinct and is not editable, mirroring how weekends are excluded from the Mon–Fri grid (XC-05).
    private bool DayIsHoliday(int offset) => _weekHolidays.Contains(CurrentWeek.AddDays(offset));
    public bool MonIsHoliday => DayIsHoliday(0);
    public bool TueIsHoliday => DayIsHoliday(1);
    public bool WedIsHoliday => DayIsHoliday(2);
    public bool ThuIsHoliday => DayIsHoliday(3);
    public bool FriIsHoliday => DayIsHoliday(4);
    public bool MonReadOnly => IsReadOnly || MonIsHoliday;
    public bool TueReadOnly => IsReadOnly || TueIsHoliday;
    public bool WedReadOnly => IsReadOnly || WedIsHoliday;
    public bool ThuReadOnly => IsReadOnly || ThuIsHoliday;
    public bool FriReadOnly => IsReadOnly || FriIsHoliday;

    private void NotifyDayFlags()
    {
        OnPropertyChanged(nameof(MonIsHoliday)); OnPropertyChanged(nameof(TueIsHoliday));
        OnPropertyChanged(nameof(WedIsHoliday)); OnPropertyChanged(nameof(ThuIsHoliday));
        OnPropertyChanged(nameof(FriIsHoliday));
        OnPropertyChanged(nameof(MonReadOnly)); OnPropertyChanged(nameof(TueReadOnly));
        OnPropertyChanged(nameof(WedReadOnly)); OnPropertyChanged(nameof(ThuReadOnly));
        OnPropertyChanged(nameof(FriReadOnly));
    }

    partial void OnSelectedTargetChanged(EntryTarget? value)
    {
        OnPropertyChanged(nameof(IsTeamView));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanEdit));
        NotifyDayFlags();   // IsReadOnly feeds the per-day read-only flags (ISSUE 5)
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

    public ObservableCollection<BacklogGroupVm> Groups { get; } = new();

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
        var dow = d.DayOfWeek.ToString()[..3].ToUpperInvariant(); // MON/TUE/... (matches the design)
        return $"{dow} {d:dd/MM}";
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await LoadCollapsePreferenceAsync();
        await LoadTargetsAsync();
        await ReloadAsync();
    }

    // ISSUE 5: refresh the holiday set for the currently shown Mon–Fri week, then re-raise the per-day
    // flags so the cells' read-only state + holiday styling update.
    private async Task LoadWeekHolidaysAsync()
    {
        _weekHolidays.Clear();
        if (_holidays is not null)
        {
            var friday = CurrentWeek.AddDays(4);
            foreach (var h in await _holidays.GetAllAsync())
                if (h.Date >= CurrentWeek && h.Date <= friday)
                    _weekHolidays.Add(h.Date);
        }
        NotifyDayFlags();
    }

    // Restore the saved "Collapse all" preference before the first reload applies it to the groups.
    private async Task LoadCollapsePreferenceAsync()
    {
        if (_settings is null) return;
        var saved = await _settings.GetAsync(CollapseAllKey);
        if (saved is not null) AllCollapsed = saved == "true";
    }

    // Quick collapse/expand of ALL backlog groups at once (Entry header button).
    [ObservableProperty] private bool _allCollapsed;

    public string CollapseToggleText => AllCollapsed ? "Expand all" : "Collapse all";
    partial void OnAllCollapsedChanged(bool value) => OnPropertyChanged(nameof(CollapseToggleText));

    [RelayCommand]
    private void ToggleCollapseAll()
    {
        AllCollapsed = !AllCollapsed;
        foreach (var g in Groups) g.IsExpanded = !AllCollapsed;
        _ = _settings?.SetAsync(CollapseAllKey, AllCollapsed ? "true" : "false"); // remember across restarts
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
        // ISSUE 5 (HOL-02): load the visible week's holidays so holiday day-columns render distinct and
        // non-editable (the service also rejects a holiday save defensively).
        await LoadWeekHolidaysAsync();

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

        // Preserve each group's expand/collapse state across reloads, keyed by BacklogId.
        var expandedById = Groups.ToDictionary(g => g.BacklogId, g => g.IsExpanded);
        foreach (var r in AllRows) r.DayChanged -= OnRowDayChanged;
        Groups.Clear();

        foreach (var grp in grouped)
        {
            var groupVm = new BacklogGroupVm(
                grp.BacklogId, grp.BacklogCode, grp.Project, _tasks, OnTaskAddedAsync,
                grp.PeriodMonth, grp.Type, grp.AssigneeName);
            if (expandedById.TryGetValue(grp.BacklogId, out var wasExpanded))
                groupVm.IsExpanded = wasExpanded;

            foreach (var wr in grp.Tasks)
            {
                var row = new TimesheetRowVm
                {
                    TaskId = wr.TaskId,
                    BacklogCode = wr.BacklogCode,
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
        if (result.Ok)
            _messenger.Send(new DataChangedMessage(DataKind.Logs)); // live-sync: Reports "missing logs" banner
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
        foreach (var g in Groups) g.RefreshTotal(); // live per-backlog header totals
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// Inline add-task callback handed to each BacklogGroupVm: after the Task is inserted, reload the
    /// grid (so the new empty row appears) and broadcast so other tabs refresh too.
    private async Task OnTaskAddedAsync()
    {
        await ReloadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    /// True when both task ids live in the same backlog group — reorder is only allowed within a group,
    /// so the view uses this to show an honest (Move vs None) drag cursor before the drop (not a Move
    /// cursor that then silently no-ops on a cross-group drop).
    public bool AreInSameGroup(int taskIdA, int taskIdB) =>
        Groups.Any(g => g.Tasks.Any(t => t.TaskId == taskIdA) && g.Tasks.Any(t => t.TaskId == taskIdB));

    /// Drag-reorder a task within its backlog group (same group only), persist order_index, then reload.
    public async Task ReorderTaskAsync(int draggedTaskId, int targetTaskId)
    {
        if (IsReadOnly || draggedTaskId == targetTaskId) return;
        var group = Groups.FirstOrDefault(g => g.Tasks.Any(t => t.TaskId == draggedTaskId));
        if (group is null || group.Tasks.All(t => t.TaskId != targetTaskId)) return;   // same group only
        var ids = group.Tasks.Select(t => t.TaskId).ToList();
        ids.Remove(draggedTaskId);
        ids.Insert(ids.IndexOf(targetTaskId), draggedTaskId);
        for (var i = 0; i < ids.Count; i++) await _tasks.SetOrderAsync(ids[i], i);
        await OnTaskAddedAsync();
    }

    /// Drag-to-trash: soft-delete a task (its time logs are preserved), then reload.
    public async Task DeleteTaskAsync(int taskId)
    {
        if (IsReadOnly) return;
        await _tasks.SetActiveAsync(taskId, false);
        await OnTaskAddedAsync();
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
    private async Task MoveMonthAsync(int backlogId)
    {
        if (_backlogs is null) return;
        var backlog = await _backlogs.GetByIdAsync(backlogId);
        if (backlog is null) return;
        // The hidden DEFAULT backlog must NEVER belong to a month — it has to appear in EVERY month
        // (it holds the recurring default tasks). Defense-in-depth: the UI already hides Move for it.
        if (string.Equals(backlog.BacklogCode, "DEFAULT", StringComparison.Ordinal)) return;

        // Base month = the ticket's current period (or the filter month if unassigned), then +1.
        var baseMonth = ParseMonth(backlog.PeriodMonth) ?? SelectedMonth;
        var next = baseMonth.AddMonths(1).ToString("yyyy-MM");

        await _backlogs.UpdateAsync(backlog with { PeriodMonth = next },
            _currentUser?.Current?.Id, _currentUser?.Current?.Name);
        await ReloadAsync();
        _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
    }

    private static DateOnly? ParseMonth(string? yyyymm) =>
        DateOnly.TryParseExact(yyyymm + "-01", "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    // Test-only hook to exercise the Applied -> reload wiring without WPF dispatcher.
    internal void RaiseSmartInputAppliedForTest() => _ = ReloadAsync();
}
