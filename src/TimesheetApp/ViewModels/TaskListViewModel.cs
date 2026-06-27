using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

/// Task List screen VM (TL-04..08, TAG-02; spec §5.3,§6,§7). Per-month overview of every non-DEFAULT
/// backlog with tracking metadata, all-time logged-hours roll-up, schedule chips + custom tag chips,
/// expandable task rows, and a Grid↔Gantt toggle (Gantt body is built in Wave 6). Transient VM.
public sealed partial class TaskListViewModel : ObservableObject
{
    private const string DefaultBacklogCode = "DEFAULT";

    private readonly IBacklogRepository _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ITimeLogRepository _timeLogs;
    private readonly ITagRepository _tagsRepo;
    private readonly IPcaContactRepository _pcaContacts;
    private readonly IUserRepository _users;
    private readonly IHolidayRepository _holidays;
    private readonly IWorkingDayCalculator _calc;
    private readonly IScheduleStateService _schedule;
    private readonly ITaskListArchiveService _archive;
    private readonly IClock _clock;
    private readonly IMessenger _messenger;

    public TaskListViewModel(
        IBacklogRepository backlogs, ITaskRepository tasks, ITimeLogRepository timeLogs,
        ITagRepository tagsRepo, IPcaContactRepository pcaContacts, IUserRepository users,
        IHolidayRepository holidays, IWorkingDayCalculator calc, IScheduleStateService schedule,
        ITaskListArchiveService archive, IClock clock, IMessenger? messenger = null)
    {
        _backlogs = backlogs;
        _tasks = tasks;
        _timeLogs = timeLogs;
        _tagsRepo = tagsRepo;
        _pcaContacts = pcaContacts;
        _users = users;
        _holidays = holidays;
        _calc = calc;
        _schedule = schedule;
        _archive = archive;
        _clock = clock;
        _messenger = messenger ?? WeakReferenceMessenger.Default;

        // Default the month selector to the current month (assign backing fields so the change
        // handlers do NOT fire a reload during construction — the first load is driven by LoadAsync).
        var today = _clock.Today;
        _selectedMonth = today.Month;
        _selectedYear = today.Year;

        // Live cross-tab sync: reload the grid when any source data changes elsewhere (a tracking
        // field saved in the Backlog editor, a task status flipped, hours logged, a tag/holiday edited).
        // static lambda + recipient arg keeps the weak ref (mirrors TimesheetViewModel).
        _messenger.Register<TaskListViewModel, DataChangedMessage>(this, static (r, m) =>
        {
            if (m.Kind is DataKind.Backlogs or DataKind.Tasks or DataKind.Logs
                or DataKind.Tags or DataKind.Holidays or DataKind.PcaContacts)
                _ = r.LoadAsync();
        });
    }

    public ObservableCollection<TaskListRowVm> Rows { get; } = new();

    // Month selector (year + month combos, mirrors the Timesheet/editor month pickers).
    [ObservableProperty] private int _selectedYear;
    [ObservableProperty] private int _selectedMonth;
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(1, 12).ToList();
    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 2, 6).ToList();

    partial void OnSelectedYearChanged(int value) => _ = LoadAsync();
    partial void OnSelectedMonthChanged(int value) => _ = LoadAsync();

    // Grid↔Gantt toggle + collapsible chart area (Gantt body is filled in Wave 6).
    [ObservableProperty] private bool _isGantt;
    [ObservableProperty] private bool _isChartCollapsed;

    // Gantt model (W6): working-day axis + one bar per row. Built at the end of LoadAsync from the
    // same rows + holiday set the grid uses, so bars and chips agree (R5). Null until first load.
    [ObservableProperty] private GanttModel? _gantt;

    // "Export this month" status line (success path / failure path).
    [ObservableProperty] private string _exportStatus = "";

    public string SelectedMonthText => $"{SelectedMonth:00}/{SelectedYear}";

    [RelayCommand]
    public async Task LoadAsync()
    {
        var monthKey = new DateOnly(SelectedYear, SelectedMonth, 1).ToString("yyyy-MM");
        var today = _clock.Today;

        // Load the lookups once per reload (A2: holiday set is built once and passed into the pure calc).
        var allBacklogs = await _backlogs.SearchAsync(null);
        var loggedByBacklog = await _timeLogs.GetLoggedHoursByBacklogAsync();
        var tagIdsByBacklog = await _backlogs.GetTagIdsForAllAsync();
        var tagsById = (await _tagsRepo.GetAllAsync()).ToDictionary(t => t.Id);
        // GetAll (not GetActive) so a deactivated PCA/PCT still resolves on historical rows (XC-06 spirit).
        var userNames = (await _users.GetAllAsync()).ToDictionary(u => u.Id, u => u.Name);
        var pcaNames = (await _pcaContacts.GetAllAsync()).ToDictionary(p => p.Id, p => p.Name);
        var holidaySet = (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();

        var rows = new List<TaskListRowVm>();
        // Gantt source: the backlog + its computed schedule state, captured while we build the grid rows
        // so the Gantt axis/bars derive from the exact same data (R5 — bars and chips agree).
        var ganttSource = new List<(Backlog Backlog, ScheduleState State)>();
        foreach (var b in allBacklogs
                     .Where(b => !string.Equals(b.BacklogCode, DefaultBacklogCode, StringComparison.Ordinal))
                     .Where(b => b.PeriodMonth == monthKey))
        {
            var tasks = await _tasks.GetActiveByBacklogAsync(b.Id);

            var logged = loggedByBacklog.TryGetValue(b.Id, out var h) ? h : 0m;   // absent → 0
            var estimate = b.OfficialEstimateHours ?? b.RoughEstimateHours;        // §7 precedence
            // Done = ≥1 active task AND every active task Status=="Done" (§6.2). Zero tasks → not Done.
            var isDone = tasks.Count > 0 && tasks.All(t => t.Status == "Done");

            var state = _schedule.Evaluate(
                today, b.StartDate, b.DeadlineInternal, estimate, logged, isDone, holidaySet, _calc);

            var tags = tagIdsByBacklog.TryGetValue(b.Id, out var ids)
                ? ids.Where(tagsById.ContainsKey).Select(id => tagsById[id])
                    .OrderBy(t => t.Id).ToList()        // Q4: custom tags ordered by Tag.Id
                : new List<Tag>();

            var pctName = b.AssigneeUserId is { } uid && userNames.TryGetValue(uid, out var un) ? un : null;
            var pcaName = b.PcaContactId is { } pid && pcaNames.TryGetValue(pid, out var pn) ? pn : null;

            var row = new TaskListRow(
                b.Id, b.BacklogCode, b.Project, b.Type, pctName, pcaName,
                b.DeadlineInternal, b.DeadlineExternal, b.StartDate,
                b.ProgressPercent, logged, estimate, state, tags, tasks);

            rows.Add(new TaskListRowVm(row));
            ganttSource.Add((b, state));
        }

        Rows.Clear();
        foreach (var r in rows.OrderBy(r => r.Row.BacklogCode, StringComparer.OrdinalIgnoreCase))
            Rows.Add(r);

        // Build the Gantt last, from the loaded backlogs + the holiday set used above (W6).
        Gantt = BuildGantt(
            ganttSource.OrderBy(g => g.Backlog.BacklogCode, StringComparer.OrdinalIgnoreCase).ToList(),
            holidaySet, _calc);
    }

    /// <summary>
    /// Pure index math (no pixels — code-behind owns layout). Axis = the working days (weekends/holidays
    /// excluded) spanning min(start, fallback deadline) .. max(internal deadline / end). Per backlog a
    /// GanttBar: span = start → deadline_internal (Q3); missing internal but has end_date → start → end
    /// (neutral); no start_date → HasStart=false placeholder (still drawn, deadline-only). ExternalMarkerIndex
    /// = the axis position nearest deadline_external. Deterministic + unit-testable.
    /// </summary>
    internal static GanttModel BuildGantt(
        IReadOnlyList<(Backlog Backlog, ScheduleState State)> source,
        IReadOnlySet<DateOnly> holidays, IWorkingDayCalculator calc)
    {
        var empty = new GanttModel(Array.Empty<DateOnly>(), Array.Empty<GanttBar>());
        if (source.Count == 0) return empty;

        // The end of a bar drives the axis upper bound: internal deadline, else end_date (neutral fallback).
        static DateOnly? BarEnd(Backlog b) => b.DeadlineInternal ?? b.EndDate;
        // The start of a bar: the explicit start_date, else the deadline (a no-start placeholder bar).
        static DateOnly? BarStart(Backlog b) => b.StartDate ?? BarEnd(b);

        // Axis bounds across every backlog that contributes any date.
        DateOnly? min = null, max = null;
        foreach (var (b, _) in source)
        {
            if (BarStart(b) is { } sv && (min is not { } mv || sv < mv)) min = sv;
            if (BarEnd(b) is { } ev && (max is not { } xv || ev > xv)) max = ev;
        }
        if (min is not { } from || max is not { } to || from > to) return empty;

        var axis = calc.WorkingDaysBetween(from, to, holidays);
        if (axis.Count == 0) return empty;

        // index of the working day on/after `d` (clamped into [0, axis.Count-1]); axis is ascending.
        int NearestIndex(DateOnly d)
        {
            for (var i = 0; i < axis.Count; i++)
                if (axis[i] >= d) return i;
            return axis.Count - 1;
        }

        var bars = new List<GanttBar>(source.Count);
        foreach (var (b, state) in source)
        {
            var end = BarEnd(b);
            var start = BarStart(b);
            var hasStart = b.StartDate is not null;

            int startIdx, span;
            if (start is { } sv && end is { } ev)
            {
                startIdx = NearestIndex(sv);
                // Working-day count over the span, mapped to axis positions so weekends/holidays
                // inside the range are excluded (≥1 so even a same-day bar is visible).
                var endIdx = NearestIndex(ev);
                span = Math.Max(1, endIdx - startIdx + 1);
            }
            else
            {
                // No dates at all on this backlog → a zero-width placeholder pinned to the axis start.
                startIdx = 0;
                span = 0;
            }

            int? externalIdx = b.DeadlineExternal is { } ext ? NearestIndex(ext) : null;

            bars.Add(new GanttBar(
                b.Id, b.BacklogCode, b.StartDate, end, startIdx, span, externalIdx, hasStart, state));
        }

        return new GanttModel(axis, bars);
    }

    // Re-run the load for the current selection.
    [RelayCommand]
    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private static void ToggleExpand(TaskListRowVm? row)
    {
        if (row is not null) row.IsExpanded = !row.IsExpanded;
    }

    // TL-09: manual export of the selected month to its markdown overview.
    [RelayCommand]
    private async Task ExportThisMonthAsync()
    {
        try
        {
            var path = await _archive.ExportMonthAsync(SelectedYear, SelectedMonth);
            ExportStatus = path is null
                ? "Nothing to export for this month."
                : $"Exported to {path}";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
    }
}

/// One Task List grid row: wraps the immutable TaskListRow read-model and adds the view-only state
/// (expand flag + the ordered chip list bound by the chips cell). Wave 6 reads Row for the Gantt shape.
public sealed partial class TaskListRowVm : ObservableObject
{
    public TaskListRowVm(TaskListRow row)
    {
        Row = row;
        Chips = BuildChips(row);
    }

    public TaskListRow Row { get; }

    // Convenience pass-throughs for binding (keeps the XAML cells terse).
    public string BacklogCode => Row.BacklogCode;
    public string Project => Row.Project;
    public string? Type => Row.Type;
    public string? PctAssigneeName => Row.PctAssigneeName;
    public string? PcaContactName => Row.PcaContactName;
    public DateOnly? DeadlineInternal => Row.DeadlineInternal;
    public DateOnly? DeadlineExternal => Row.DeadlineExternal;
    public int? ProgressPercent => Row.ProgressPercent;
    public bool HasProgress => Row.ProgressPercent is not null;
    // null progress → "—"; otherwise the whole-number percent.
    public string ProgressText => Row.ProgressPercent is { } p ? $"{p}%" : "—";
    // Whole-number hours (§7): drop the decimals.
    public string LoggedHoursText => $"{Row.LoggedHours:0}";
    public string EstimateText => Row.EstimateHours is { } e ? $"{e:0}" : "—";
    public IReadOnlyList<TaskItem> Tasks => Row.Tasks;

    [ObservableProperty] private bool _isExpanded;

    public IReadOnlyList<TaskListChipVm> Chips { get; }

    // Chip order (TAG-02 / Q4): the single system chip first (Late before Warning — only one ever shows),
    // then custom tags already ordered by Tag.Id.
    private static IReadOnlyList<TaskListChipVm> BuildChips(TaskListRow row)
    {
        var chips = new List<TaskListChipVm>();
        if (row.ScheduleState == ScheduleState.Late)
            chips.Add(TaskListChipVm.System("⚠ Late", isLate: true));
        else if (row.ScheduleState == ScheduleState.Warning)
            chips.Add(TaskListChipVm.System("⚠ At risk", isLate: false));

        foreach (var t in row.Tags)
            chips.Add(TaskListChipVm.Custom(t));
        return chips;
    }
}

/// One chip in the chips cell. System chips (Late/Warning) use fixed amber/red theme styling (the XAML
/// keys off IsSystem + IsLate); custom tags carry their hex color + icon glyph for HexToBrush binding.
public sealed class TaskListChipVm
{
    private TaskListChipVm(bool isSystem, bool isLate, string text, string? icon, string? color)
    {
        IsSystem = isSystem;
        IsLate = isLate;
        Text = text;
        Icon = icon;
        Color = color;
    }

    public bool IsSystem { get; }
    public bool IsLate { get; }     // only meaningful when IsSystem (true=red Late, false=amber Warning)
    public string Text { get; }
    public string? Icon { get; }    // custom tag glyph/emoji
    public string? Color { get; }   // custom tag hex color (bound via HexToBrush)

    public static TaskListChipVm System(string text, bool isLate) =>
        new(isSystem: true, isLate: isLate, text, icon: null, color: null);

    public static TaskListChipVm Custom(Tag tag) =>
        new(isSystem: false, isLate: false, tag.Text, tag.Icon, tag.Color);
}
