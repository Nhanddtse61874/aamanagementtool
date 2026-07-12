// src/TimesheetApp/ViewModels/SmartInputPanelVm.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

// SmartInputMode now lives in TimesheetApp.Services (Core) alongside ISmartInputService — the fill
// contract belongs with the fill math, not in a WPF ViewModel.

/// One selectable task in the smart-fill task list (checkbox).
public sealed partial class SmartTaskItem : ObservableObject
{
    public int TaskId { get; init; }
    public string TaskName { get; init; } = "";
    [ObservableProperty] private bool _isChecked;
}

/// One preview row: which task / day / hours the fill will write.
public sealed record SmartPreviewRow(string TaskName, DateOnly Date, decimal Hours);

/// Smart-fill panel (SI redesign): enter a backlog code -> load its tasks as checkboxes -> check the
/// ones to fill -> pick From/To + total hours -> preview -> apply. Total hours (Split evenly) and the
/// 8h/day cap are spread across ALL checked tasks; apply is atomic (SI-05).
public sealed partial class SmartInputPanelVm : ObservableObject
{
    private readonly ITimeLogService _timeLogs;
    private readonly IBacklogRepository? _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly ISmartInputService _smartInput;   // M8.2: the ONE fill implementation (holiday-aware)
    private readonly IHolidayRepository? _holidays;    // HOL-02: a marked holiday is not a working day
    private readonly Func<int> _currentUserId;
    private readonly Func<int>? _currentTeamId;   // TM-06: scope the backlog search to the active team

    private List<SmartFillTask> _planned = new();

    public SmartInputPanelVm(
        ITimeLogService timeLogs, IBacklogRepository? backlogs, ITaskRepository tasks,
        ISmartInputService smartInput, Func<int> currentUserId,
        IHolidayRepository? holidays = null, Func<int>? currentTeamId = null)
    {
        _timeLogs = timeLogs;
        _backlogs = backlogs;
        _tasks = tasks;
        _smartInput = smartInput;
        _holidays = holidays;
        _currentUserId = currentUserId;
        _currentTeamId = currentTeamId;
    }

    [ObservableProperty] private string _backlogCode = string.Empty;
    [ObservableProperty] private string? _loadError;

    /// Tasks of the found request, shown as checkboxes.
    public ObservableCollection<SmartTaskItem> Tasks { get; } = new();

    [ObservableProperty] private SmartInputMode _mode = SmartInputMode.DistributeEven;

    /// True only in Split-evenly mode — the Total hours box applies only then (Full 8h ignores it).
    public bool IsSplitEven => Mode == SmartInputMode.DistributeEven;
    partial void OnModeChanged(SmartInputMode value) => OnPropertyChanged(nameof(IsSplitEven));

    [ObservableProperty] private DateOnly _from;
    [ObservableProperty] private DateOnly _to;
    [ObservableProperty] private decimal _totalHours;
    [ObservableProperty] private string? _previewError;

    public ObservableCollection<SmartPreviewRow> PreviewCells { get; } = new();

    /// True only after a preview that produced cells AND passed the 8h day-total validation.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    /// Raised after a successful atomic apply; owner VM reloads the week grid.
    public event Action? Applied;

    /// Find backlogs whose code/project CONTAINS the term (partial match) and load their active tasks
    /// as checkboxes. When more than one backlog matches, each task is prefixed with its backlog code.
    [RelayCommand]
    private async Task FindBacklogAsync()
    {
        Tasks.Clear();
        PreviewCells.Clear();
        LoadError = null;
        PreviewError = null;
        CanApply = false;

        var term = BacklogCode?.Trim();
        if (string.IsNullOrEmpty(term)) { LoadError = "Type part of a backlog code to search."; return; }
        if (_backlogs is null) { LoadError = "Backlog lookup is unavailable."; return; }

        // Contains search (LIKE %term%) on backlog_code OR project; hide the internal DEFAULT backlog.
        // TM-06: scope to the active team so Smart Fill never surfaces (or writes to) another team's tasks.
        var teamIds = _currentTeamId is null ? null : new[] { _currentTeamId() };
        var matches = (await _backlogs.SearchAsync(term, teamIds))
            .Where(b => !string.Equals(b.BacklogCode, "DEFAULT", StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0) { LoadError = $"No backlog matches '{term}'."; return; }

        var prefixWithCode = matches.Count > 1;
        foreach (var bl in matches)
        {
            var tasks = await _tasks.GetActiveByBacklogAsync(bl.Id);
            foreach (var t in tasks.OrderBy(t => t.OrderIndex))
                Tasks.Add(new SmartTaskItem
                {
                    TaskId = t.Id,
                    TaskName = prefixWithCode ? $"{bl.BacklogCode} · {t.TaskName}" : t.TaskName,
                });
        }
        if (Tasks.Count == 0) LoadError = "Matching backlog(s) have no tasks.";
    }

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        CanApply = false;
        PreviewCells.Clear();
        PreviewError = null;
        _planned = new();

        var selected = Tasks.Where(t => t.IsChecked).ToList();
        if (selected.Count == 0) { PreviewError = "Select at least one task."; return; }

        // HOL-02: preview the SAME working days the apply-side validator enforces. Passing the holiday
        // set here is what stops a holiday from being previewed as a cell and then rejected on Apply.
        var holidays = _holidays is null
            ? new HashSet<DateOnly>()
            : (await _holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();

        var result = _smartInput.BuildPlan(
            Mode, From, To, TotalHours, selected.Select(s => s.TaskId).ToList(), holidays);
        if (!result.Ok) { PreviewError = result.Error; return; }
        _planned = result.Tasks.ToList();

        foreach (var t in _planned)
        {
            var name = selected.First(s => s.TaskId == t.TaskId).TaskName;
            foreach (var c in t.Cells) PreviewCells.Add(new SmartPreviewRow(name, c.Date, c.Hours));
        }

        var validation = await _timeLogs.ValidateSmartFillAsync(_currentUserId(), _planned);
        if (!validation.Ok) { PreviewError = validation.Error; return; }
        CanApply = true;
    }

    [RelayCommand] private void SetDistributeEven() => Mode = SmartInputMode.DistributeEven;
    [RelayCommand] private void SetFull8h() => Mode = SmartInputMode.FillFull8h;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!CanApply) return; // never write without a validated preview (SI-05)

        var result = await _timeLogs.ApplySmartFillAsync(_currentUserId(), _planned);
        if (!result.Ok) { PreviewError = result.Error; return; }

        CanApply = false;
        PreviewCells.Clear();
        Applied?.Invoke();
    }

}
