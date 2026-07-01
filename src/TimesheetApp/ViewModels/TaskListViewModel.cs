using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
// Disambiguate from System.Threading.Tasks.TaskStatus (in scope via async Task usage).
using TaskStatus = TimesheetApp.Models.TaskStatus;

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
    // Set only while broadcasting the VM's own inline tag commit, so the self-registered reload handler
    // skips the rebuild (keeps an open TagPicker alive across a multi-tag selection). See SendKeepingPicker.
    private bool _suppressSelfReload;
    private readonly ICurrentTeamService? _currentTeam;   // v8 (P10): active team + multi-team filter
    private readonly ICurrentUserService? _currentUser;   // v9 (P13-W3): audit who-changed on inline edits

    public TaskListViewModel(
        IBacklogRepository backlogs, ITaskRepository tasks, ITimeLogRepository timeLogs,
        ITagRepository tagsRepo, IPcaContactRepository pcaContacts, IUserRepository users,
        IHolidayRepository holidays, IWorkingDayCalculator calc, IScheduleStateService schedule,
        ITaskListArchiveService archive, IClock clock, IMessenger? messenger = null,
        ICurrentTeamService? currentTeam = null, ICurrentUserService? currentUser = null)
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
        _currentTeam = currentTeam;
        _currentUser = currentUser;

        // P10 (TM-07): the multi-team checkbox filter; reload the grid on a selection/active-team change.
        if (_currentTeam is not null)
        {
            TeamFilter = new TeamFilterViewModel(_currentTeam);
            TeamFilter.SelectionChanged += (_, _) => _ = LoadAsync();
        }

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
            // Skip our OWN inline tag commit — a full rebuild here would destroy the open TagPicker popup
            // mid-multi-select (you could only ever check one tag). External tag changes still reload.
            if (r._suppressSelfReload) return;
            if (m.Kind is DataKind.Backlogs or DataKind.Tasks or DataKind.Logs
                or DataKind.Tags or DataKind.Holidays or DataKind.PcaContacts)
                _ = r.LoadAsync();
        });
    }

    public ObservableCollection<TaskListRowVm> Rows { get; } = new();

    // P10: the shared multi-team filter (null when no current-team service is wired — legacy ctor / tests).
    public TeamFilterViewModel? TeamFilter { get; }

    // v9 (P13-W3): the current user for audit (null on the legacy ctor / tests → audit uid/name pass null).
    internal int? CurrentUserId => _currentUser?.Current?.Id;
    internal string? CurrentUserName => _currentUser?.Current?.Name;

    // ---- v9 (P13-W3) inline-edit option lists (populated in LoadAsync so cells can bind) -----------
    // Backlog/task TYPE choices (BacklogType.All; tasks reuse the same list).
    public IReadOnlyList<string> TypeOptions { get; private set; } = BacklogType.All;
    // Task STATUS choices (the four TaskStatus values).
    public IReadOnlyList<string> StatusOptions { get; private set; } = TaskStatus.All;
    // PCT (assignee) choices: a "—" unassigned sentinel (Id = null) + every user.
    public IReadOnlyList<EditOption> AssigneeOptions { get; private set; } = Array.Empty<EditOption>();
    // PCA (external contact) choices: a "—" none sentinel (Id = null) + every PCA contact.
    public IReadOnlyList<EditOption> PcaOptions { get; private set; } = Array.Empty<EditOption>();
    // Every custom tag (ordered by Id) — seeds the per-row/per-task checkable tag pickers.
    public IReadOnlyList<Tag> AllTags { get; private set; } = Array.Empty<Tag>();

    // ISSUE 3: sentinel month meaning "All months" — shows every backlog (any period_month, incl. null),
    // matching the Backlog tab. 0 sorts before 1-12 so it heads the combo.
    public const int AllMonths = 0;

    // Month selector (year + month combos, mirrors the Timesheet/editor month pickers).
    [ObservableProperty] private int _selectedYear;
    [ObservableProperty] private int _selectedMonth;
    // 0 = "All months" (ISSUE 3), then the 12 calendar months.
    public IReadOnlyList<int> Months { get; } = Enumerable.Range(0, 13).ToList();
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

    public string SelectedMonthText =>
        SelectedMonth == AllMonths ? "All months" : $"{SelectedMonth:00}/{SelectedYear}";

    [RelayCommand]
    public async Task LoadAsync()
    {
        // ISSUE 3: "All months" (SelectedMonth == AllMonths) lists every backlog regardless of period
        // (incl. null period_month), like the Backlog tab; otherwise filter to the selected yyyy-MM.
        var allMonths = SelectedMonth == AllMonths;
        var monthKey = allMonths ? null : new DateOnly(SelectedYear, SelectedMonth, 1).ToString("yyyy-MM");
        var today = _clock.Today;

        // Load the lookups once per reload (A2: holiday set is built once and passed into the pure calc).
        // P10 (TM-07): scope to the checked teams (null = all teams = legacy/no-filter behavior).
        var teamIds = TeamFilter?.CheckedTeamIds;
        var allBacklogs = await _backlogs.SearchAsync(null, teamIds);
        var loggedByBacklog = await _timeLogs.GetLoggedHoursByBacklogAsync();
        var tagIdsByBacklog = await _backlogs.GetTagIdsForAllAsync();
        var allTags = (await _tagsRepo.GetAllAsync()).OrderBy(t => t.Id).ToList();
        var tagsById = allTags.ToDictionary(t => t.Id);
        // GetAll (not GetActive) so a deactivated PCA/PCT still resolves on historical rows (XC-06 spirit).
        var allUsers = (await _users.GetAllAsync()).ToList();
        var allPca = (await _pcaContacts.GetAllAsync()).ToList();
        var userNames = allUsers.ToDictionary(u => u.Id, u => u.Name);
        var pcaNames = allPca.ToDictionary(p => p.Id, p => p.Name);
        var holidaySet = (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();

        // v9 (P13-W3): build the inline-edit option lists from the data already fetched above (no N+1).
        // PCT/PCA lists lead with a null-id "—" sentinel meaning unassigned/none.
        AllTags = allTags;
        TypeOptions = BacklogType.All;
        StatusOptions = TaskStatus.All;
        AssigneeOptions = new[] { EditOption.None }
            .Concat(allUsers.Select(u => new EditOption(u.Id, u.Name))).ToList();
        PcaOptions = new[] { EditOption.None }
            .Concat(allPca.Select(p => new EditOption(p.Id, p.Name))).ToList();

        // P10: id -> team name (from the user's available teams) + label visibility when >1 team is checked.
        var teamNames = _currentTeam?.AvailableTeams.ToDictionary(t => t.Id, t => t.Name)
                        ?? new Dictionary<int, string>();
        var showTeam = TeamFilter?.ShowTeamColumn ?? false;

        var filteredBacklogs = allBacklogs
            .Where(b => !string.Equals(b.BacklogCode, DefaultBacklogCode, StringComparison.Ordinal))
            .Where(b => allMonths || b.PeriodMonth == monthKey)
            .ToList();
        var filteredIds = filteredBacklogs.Select(b => b.Id).ToList();
        var tasksByBacklog = await _tasks.GetActiveByBacklogsAsync(filteredIds);

        // Preserve which rows are expanded across the rebuild — an inline edit re-broadcasts a
        // DataChangedMessage that reloads the grid; without this, editing a sub-row field would collapse
        // its detail panel (rebuilt rows default to IsExpanded=false).
        var expandedBacklogIds = Rows.Where(r => r.IsExpanded).Select(r => r.BacklogId).ToHashSet();

        var rows = new List<TaskListRowVm>();
        // Gantt source: the backlog + its computed schedule state, captured while we build the grid rows
        // so the Gantt axis/bars derive from the exact same data (R5 — bars and chips agree).
        var ganttSource = new List<(Backlog Backlog, ScheduleState State)>();
        foreach (var b in filteredBacklogs)
        {
            tasksByBacklog.TryGetValue(b.Id, out var tasks);
            var taskList = tasks ?? Array.Empty<TaskItem>();

            var logged = loggedByBacklog.TryGetValue(b.Id, out var h) ? h : 0m;   // absent → 0
            var estimate = b.OfficialEstimateHours ?? b.RoughEstimateHours;        // §7 precedence
            // Done = ≥1 active task AND every active task Status=="Done" (§6.2). Zero tasks → not Done.
            var isDone = taskList.Count > 0 && taskList.All(t => t.Status == "Done");

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
                b.DeadlineInternal, b.DeadlineExternal, b.StartDate, b.EndDate,
                b.ProgressPercent, logged, estimate, state, tags, taskList);

            // v9 (P13-W3): editable task sub-rows — each carries its own current type/assignee/tags so the
            // expand panel can edit them inline. Tag ids are loaded per task via the existing single fetch
            // (TaskItem carries no tags); the set is small (active tasks of the visible backlogs).
            var taskRows = new List<TaskRowVm>(taskList.Count);
            foreach (var t in taskList)
            {
                var tTagIds = await _tasks.GetTagIdsAsync(t.Id);
                taskRows.Add(new TaskRowVm(this, t, tTagIds));
            }

            var teamName = b.TeamId is { } tid && teamNames.TryGetValue(tid, out var tn) ? tn : null;
            // Pass the owning VM + the backlog's current ids/tag-id set so the row's edit props seed correctly.
            rows.Add(new TaskListRowVm(
                this, row, b.AssigneeUserId, b.PcaContactId,
                tagIdsByBacklog.TryGetValue(b.Id, out var bIds) ? bIds : Array.Empty<int>(),
                taskRows, teamName, showTeam));
            ganttSource.Add((b, state));
        }

        Rows.Clear();
        foreach (var r in rows.OrderBy(r => r.Row.BacklogCode, StringComparer.OrdinalIgnoreCase))
        {
            if (expandedBacklogIds.Contains(r.BacklogId)) r.IsExpanded = true;   // restore expansion
            Rows.Add(r);
        }

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
        if (SelectedMonth == AllMonths)
        {
            // Export is per-month; pick a concrete month first (the markdown overview is month-scoped).
            ExportStatus = "Pick a specific month to export.";
            return;
        }

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

    // ---- v9 (P13-W3) inline backlog edits (called from TaskListRowVm setters / the deadline popup) ----

    /// <summary>
    /// Commit one row's inline backlog edits (Type / PCT / PCA / Progress) — load the backlog, copy the
    /// row's current edit values onto it, persist with field-diff audit (note = null, these are NOT
    /// deadline fields), then refresh the grid. Tags + deadlines have their own commit paths.
    /// </summary>
    internal async Task CommitBacklogEditAsync(TaskListRowVm row)
    {
        var backlog = await _backlogs.GetByIdAsync(row.BacklogId);
        if (backlog is null) return;

        var updated = backlog with
        {
            Type = row.EditType,
            AssigneeUserId = row.EditPctUserId,
            PcaContactId = row.EditPcaId,
            ProgressPercent = row.EditProgress,
        };
        await _backlogs.UpdateAsync(updated, CurrentUserId, CurrentUserName);
        _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
    }

    /// <summary>
    /// Commit a row's checked-tag set (the TagPicker replace-set) — SetTagsAsync diff-audits internally.
    /// </summary>
    internal async Task CommitBacklogTagsAsync(int backlogId, IReadOnlyList<int> tagIds)
    {
        await _backlogs.SetTagsAsync(backlogId, tagIds, CurrentUserId, CurrentUserName);
        SendKeepingPicker(DataKind.Tags);
    }

    /// <summary>
    /// Commit a deadline change (internal or external) with the reason note captured by the View's popup.
    /// Loads the backlog, sets the chosen deadline field, persists with the note on the audit row, refreshes.
    /// Not driven by a setter (the View shows its note popup first, then calls this).
    /// </summary>
    public async Task CommitDeadlineAsync(int backlogId, bool isInternal, DateOnly? newDate, string? note)
    {
        var backlog = await _backlogs.GetByIdAsync(backlogId);
        if (backlog is null) return;

        var updated = isInternal
            ? backlog with { DeadlineInternal = newDate }
            : backlog with { DeadlineExternal = newDate };
        await _backlogs.UpdateAsync(updated, CurrentUserId, CurrentUserName, auditNote: note);
        _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
    }

    /// <summary>
    /// Commit a backlog's operational start/end date (edited inline in the Task List, not the Backlog
    /// editor — business rule: the editor holds only default fields). No reason note, unlike deadlines.
    /// </summary>
    public async Task CommitStartEndAsync(int backlogId, bool isStart, DateOnly? newDate)
    {
        var backlog = await _backlogs.GetByIdAsync(backlogId);
        if (backlog is null) return;

        var updated = isStart
            ? backlog with { StartDate = newDate }
            : backlog with { EndDate = newDate };
        await _backlogs.UpdateAsync(updated, CurrentUserId, CurrentUserName);
        _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
    }

    // ---- v9 (P13-W3) inline task edits (called from TaskRowVm setters) -------------------------------

    /// <summary>Commit a task's type + assignee (one row UPDATE) with field-diff audit, then refresh.</summary>
    internal async Task UpdateTaskExtendedAsync(int taskId, string? type, int? assigneeUserId)
    {
        await _tasks.UpdateExtendedAsync(taskId, type, assigneeUserId, CurrentUserId, CurrentUserName);
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    /// <summary>Commit a task's status with audit, then refresh.</summary>
    internal async Task UpdateTaskStatusAsync(int taskId, string status)
    {
        await _tasks.UpdateStatusAsync(taskId, status, CurrentUserId, CurrentUserName);
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    /// <summary>Commit a task's checked-tag set (SetTaskTagsAsync diff-audits internally), then refresh.</summary>
    internal async Task UpdateTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds)
    {
        await _tasks.SetTaskTagsAsync(taskId, tagIds, CurrentUserId, CurrentUserName);
        SendKeepingPicker(DataKind.Tags);
    }

    // Broadcast a tag change WITHOUT triggering our own reload — the WeakReferenceMessenger delivers
    // synchronously, so the flag reliably scopes just our self-handler. Other tabs still reload; our own
    // open TagPicker survives so the user can toggle several tags in one go.
    private void SendKeepingPicker(DataKind kind)
    {
        _suppressSelfReload = true;
        try { _messenger.Send(new DataChangedMessage(kind)); }
        finally { _suppressSelfReload = false; }
    }
}

/// v9 (P13-W3): one choice in a PCT/PCA inline ComboBox. Id is null for the "—" unassigned/none sentinel,
/// otherwise the user/contact id. Bound via SelectedValuePath="Id" so null ⇄ the sentinel row directly.
public sealed record EditOption(int? Id, string Name)
{
    public static readonly EditOption None = new(null, "—");
}

/// One Task List grid row: wraps the immutable TaskListRow read-model and adds the view-only state
/// (expand flag + the ordered chip list bound by the chips cell). Wave 6 reads Row for the Gantt shape.
public sealed partial class TaskListRowVm : ObservableObject
{
    // v9 (P13-W3): back-ref to the owning VM (option lists + commit methods + current-user for audit).
    private readonly TaskListViewModel? _owner;
    // COMMIT-ON-LOAD GUARD: true while the row is being built so seeding the edit props from current
    // values does NOT fire a save. Genuine user edits (after construction) flip the guard off and commit.
    private readonly bool _suppressCommit;

    public TaskListRowVm(TaskListRow row, string? teamName = null, bool showTeam = false)
    {
        // Legacy ctor (kept so existing tests/constructors compile) — no owner ⇒ read-only, edits no-op.
        Row = row;
        Chips = BuildChips(row);
        TeamName = teamName;
        ShowTeam = showTeam;
        TaskRows = Array.Empty<TaskRowVm>();
        EditTagPicks = new ObservableCollection<TagPickVm>();
    }

    // v9 (P13-W3): full ctor — owner + the backlog's current ids/tag-id set + its editable task sub-rows.
    public TaskListRowVm(
        TaskListViewModel owner, TaskListRow row,
        int? pctUserId, int? pcaId, IReadOnlyList<int> tagIds,
        IReadOnlyList<TaskRowVm> taskRows, string? teamName = null, bool showTeam = false)
    {
        _suppressCommit = true;   // seeding initial values must not commit

        _owner = owner;
        Row = row;
        Chips = BuildChips(row);
        TeamName = teamName;
        ShowTeam = showTeam;
        TaskRows = taskRows;

        // Seed the inline-edit props from the row's current values (write backing fields / the auto-prop
        // setter directly so no OnXxxChanged fires during construction — belt-and-braces with _suppressCommit).
        _editType = row.Type;
        _editPctUserId = pctUserId;
        _editPcaId = pcaId;
        EditProgress = row.ProgressPercent;
        _editProgressText = row.ProgressPercent?.ToString() ?? string.Empty;

        // Tag multi-select (TagPicker shape): every tag, checked when currently linked to this backlog.
        var checkedSet = new HashSet<int>(tagIds);
        EditTagPicks = new ObservableCollection<TagPickVm>(
            owner.AllTags.Select(t => new TagPickVm(t, checkedSet.Contains(t.Id))));
        // After the initial seed, a CheckBox toggle is a genuine edit → commit the whole set.
        foreach (var pick in EditTagPicks)
            pick.PropertyChanged += OnTagPickChanged;

        _suppressCommit = false;  // construction done — subsequent setters commit
    }

    public TaskListRow Row { get; }

    public int BacklogId => Row.BacklogId;

    // P10 (TM-07): the owning team's name + whether to show it (only when >1 team is checked).
    public string? TeamName { get; }
    public bool ShowTeam { get; }

    // Convenience pass-throughs for binding (keeps the XAML cells terse).
    public string BacklogCode => Row.BacklogCode;
    public string Project => Row.Project;
    public string? Type => Row.Type;
    public string? PctAssigneeName => Row.PctAssigneeName;
    public string? PcaContactName => Row.PcaContactName;
    public DateOnly? DeadlineInternal => Row.DeadlineInternal;
    public DateOnly? DeadlineExternal => Row.DeadlineExternal;
    // v9 operational dates edited inline in the Task List (moved out of the Backlog editor per business rule).
    public DateOnly? StartDate => Row.StartDate;
    public DateOnly? EndDate => Row.EndDate;
    public int? ProgressPercent => Row.ProgressPercent;
    public bool HasProgress => Row.ProgressPercent is not null;
    // null progress → "—"; otherwise the whole-number percent.
    public string ProgressText => Row.ProgressPercent is { } p ? $"{p}%" : "—";
    // Whole-number hours (§7): drop the decimals.
    public string LoggedHoursText => $"{Row.LoggedHours:0}";
    public string EstimateText => Row.EstimateHours is { } e ? $"{e:0}" : "—";
    public IReadOnlyList<TaskItem> Tasks => Row.Tasks;
    // Drives the expand row-detail empty-state hint (a backlog with no active tasks).
    public bool HasNoTasks => Row.Tasks.Count == 0;

    [ObservableProperty] private bool _isExpanded;

    // Progress cell edit-mode toggle: false shows the % bar; true swaps in the 0-100 number input.
    // The View flips this true on click and back to false on Enter / lost-focus (the actual persist still
    // happens through EditProgressText → Commit, unchanged).
    [ObservableProperty] private bool _isEditingProgress;

    public IReadOnlyList<TaskListChipVm> Chips { get; }

    // ---- v9 (P13-W3) inline-edit option lists (forwarded from the owner so cells bind on the row) ----
    public IReadOnlyList<string> TypeOptions => _owner?.TypeOptions ?? BacklogType.All;
    public IReadOnlyList<EditOption> AssigneeOptions => _owner?.AssigneeOptions ?? Array.Empty<EditOption>();
    public IReadOnlyList<EditOption> PcaOptions => _owner?.PcaOptions ?? Array.Empty<EditOption>();

    // Editable task sub-rows shown when the row is expanded (replaces the raw Tasks pass-through).
    public IReadOnlyList<TaskRowVm> TaskRows { get; }

    // TagPicker-bound checkable set (items: settable IsChecked + Tag); committing replaces the link set.
    public ObservableCollection<TagPickVm> EditTagPicks { get; }

    // ---- v9 (P13-W3) inline-edit properties (TwoWay-settable; commit on genuine user edits) ----------
    // TYPE (BacklogType string, null clears).
    [ObservableProperty] private string? _editType;
    // PCT assignee user id (null = unassigned sentinel).
    [ObservableProperty] private int? _editPctUserId;
    // PCA contact id (null = none sentinel).
    [ObservableProperty] private int? _editPcaId;
    // Parsed progress 0-100 (null = cleared); kept in sync with EditProgressText.
    public int? EditProgress { get; private set; }
    // Progress edit as text — parses to EditProgress (0-100), commits on a valid/cleared change.
    [ObservableProperty] private string _editProgressText = string.Empty;

    partial void OnEditTypeChanged(string? value) => Commit();
    partial void OnEditPctUserIdChanged(int? value) => Commit();
    partial void OnEditPcaIdChanged(int? value) => Commit();

    // Set true only during ResetProgressEdit so restoring the committed value doesn't persist it back.
    private bool _suppressProgressCommit;

    partial void OnEditProgressTextChanged(string value)
    {
        // Empty → cleared (null). Whole number 0-100 → that value. Anything else is invalid → no commit.
        if (string.IsNullOrWhiteSpace(value))
        {
            EditProgress = null;
        }
        else if (int.TryParse(value.Trim(), out var p) && p is >= 0 and <= 100)
        {
            EditProgress = p;
        }
        else
        {
            return;   // invalid input — leave EditProgress as-is and do not commit
        }
        if (_suppressProgressCommit) return;   // Escape-restore: update the value, don't persist
        Commit();
    }

    // Escape from the progress input: restore the last committed value without persisting it back, so a
    // half-typed / cancelled edit leaves the stored percent untouched.
    public void ResetProgressEdit()
    {
        _suppressProgressCommit = true;
        EditProgressText = Row.ProgressPercent?.ToString() ?? string.Empty;   // re-parses into EditProgress
        _suppressProgressCommit = false;
    }

    // Persist Type/PCT/PCA/Progress via the owner (skips while seeding / on the legacy ctor). The genuine
    // user edit arrives by a code-behind combo handler setting the edit prop (a CellTemplate combo's own
    // TwoWay write never reaches the row — see TaskListTab.xaml.cs).
    private void Commit()
    {
        if (_suppressCommit || _owner is null) return;
        _ = _owner.CommitBacklogEditAsync(this);
    }

    // A tag CheckBox toggled (after seeding) → replace the whole link set with the now-checked tags.
    private void OnTagPickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressCommit || _owner is null) return;
        if (e.PropertyName != nameof(TagPickVm.IsChecked)) return;
        var ids = EditTagPicks.Where(t => t.IsChecked).Select(t => t.Tag.Id).ToList();
        _ = _owner.CommitBacklogTagsAsync(BacklogId, ids);
    }

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

/// v9 (P13-W3): one editable task sub-row in the expand panel. Wraps a TaskItem and adds TwoWay-settable
/// inline edits (Type / PCT assignee / Status + a checkable tag set) that commit to TaskRepository with
/// who-changed audit via the owning VM. Same COMMIT-ON-LOAD guard as TaskListRowVm.
public sealed partial class TaskRowVm : ObservableObject
{
    private readonly TaskListViewModel _owner;
    private readonly bool _suppressCommit;

    public TaskRowVm(TaskListViewModel owner, TaskItem task, IReadOnlyList<int> tagIds)
    {
        _suppressCommit = true;   // seeding initial values must not commit

        _owner = owner;
        TaskId = task.Id;
        TaskName = task.TaskName;

        _editType = task.Type;
        _editPctUserId = task.AssigneeUserId;
        _editStatus = task.Status;

        var checkedSet = new HashSet<int>(tagIds);
        EditTagPicks = new ObservableCollection<TagPickVm>(
            owner.AllTags.Select(t => new TagPickVm(t, checkedSet.Contains(t.Id))));
        foreach (var pick in EditTagPicks)
            pick.PropertyChanged += OnTagPickChanged;

        _suppressCommit = false;
    }

    public int TaskId { get; }
    public string TaskName { get; }

    // Option lists forwarded from the owner (no per-row copies).
    public IReadOnlyList<string> TypeOptions => _owner.TypeOptions;
    public IReadOnlyList<EditOption> AssigneeOptions => _owner.AssigneeOptions;
    public IReadOnlyList<string> StatusOptions => _owner.StatusOptions;

    // TagPicker-bound checkable set (settable IsChecked + Tag) — committing replaces the task's link set.
    public ObservableCollection<TagPickVm> EditTagPicks { get; }

    // TYPE (mirrors Backlog.Type; null clears) → UpdateExtendedAsync(type, assignee).
    [ObservableProperty] private string? _editType;
    // PCT assignee user id (null = unassigned) → UpdateExtendedAsync(type, assignee).
    [ObservableProperty] private int? _editPctUserId;
    // STATUS (one of TaskStatus.All) → UpdateStatusAsync.
    [ObservableProperty] private string _editStatus = "Todo";

    partial void OnEditTypeChanged(string? value) => CommitExtended();
    partial void OnEditPctUserIdChanged(int? value) => CommitExtended();
    partial void OnEditStatusChanged(string value) => CommitStatus();

    // type + assignee live on the same row UPDATE (UpdateExtendedAsync), so both setters route here.
    private void CommitExtended()
    {
        if (_suppressCommit) return;
        _ = _owner.UpdateTaskExtendedAsync(TaskId, EditType, EditPctUserId);
    }

    private void CommitStatus()
    {
        if (_suppressCommit) return;
        _ = _owner.UpdateTaskStatusAsync(TaskId, EditStatus);
    }

    private void OnTagPickChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressCommit) return;
        if (e.PropertyName != nameof(TagPickVm.IsChecked)) return;
        var ids = EditTagPicks.Where(t => t.IsChecked).Select(t => t.Tag.Id).ToList();
        _ = _owner.UpdateTaskTagsAsync(TaskId, ids);
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
