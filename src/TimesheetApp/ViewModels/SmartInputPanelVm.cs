// src/TimesheetApp/ViewModels/SmartInputPanelVm.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public enum SmartInputMode { DistributeEven, FillFull8h }

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
    private const int DayCapTenths = 80; // 8.0h

    private readonly ITimeLogService _timeLogs;
    private readonly IBacklogRepository? _backlogs;
    private readonly ITaskRepository _tasks;
    private readonly Func<int> _currentUserId;

    private List<SmartFillTask> _planned = new();

    public SmartInputPanelVm(
        ITimeLogService timeLogs, IBacklogRepository? backlogs, ITaskRepository tasks, Func<int> currentUserId)
    {
        _timeLogs = timeLogs;
        _backlogs = backlogs;
        _tasks = tasks;
        _currentUserId = currentUserId;
    }

    [ObservableProperty] private string _backlogCode = string.Empty;
    [ObservableProperty] private string? _loadError;

    /// Tasks of the found request, shown as checkboxes.
    public ObservableCollection<SmartTaskItem> Tasks { get; } = new();

    [ObservableProperty] private SmartInputMode _mode = SmartInputMode.DistributeEven;
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

    /// Find the backlog by code and load its active tasks as checkboxes.
    [RelayCommand]
    private async Task FindBacklogAsync()
    {
        Tasks.Clear();
        PreviewCells.Clear();
        LoadError = null;
        PreviewError = null;
        CanApply = false;

        var code = BacklogCode?.Trim();
        if (string.IsNullOrEmpty(code)) { LoadError = "Enter a backlog code."; return; }
        if (_backlogs is null) { LoadError = "Backlog lookup is unavailable."; return; }

        var backlog = await _backlogs.GetByCodeAsync(code);
        if (backlog is null) { LoadError = $"Backlog '{code}' not found."; return; }

        var tasks = await _tasks.GetActiveByBacklogAsync(backlog.Id);
        foreach (var t in tasks.OrderBy(t => t.OrderIndex))
            Tasks.Add(new SmartTaskItem { TaskId = t.Id, TaskName = t.TaskName });
        if (Tasks.Count == 0) LoadError = "This backlog has no tasks.";
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

        var (plan, error) = BuildPlan(selected);
        if (error is not null) { PreviewError = error; return; }
        _planned = plan;

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

    // Distribute either the total hours (Split evenly) or 8h/day (Full 8h) across the checked tasks ×
    // working days, in integer tenths to avoid float drift. Returns one SmartFillTask per task.
    private (List<SmartFillTask> Plan, string? Error) BuildPlan(List<SmartTaskItem> selected)
    {
        var days = WorkingDays(From, To);
        if (days.Count == 0) return (new(), "No working days in the selected range.");
        var n = selected.Count;

        // Per-task total tenths (Split evenly) — the grand total split across the checked tasks.
        int[]? perTaskTenths = null;
        if (Mode == SmartInputMode.DistributeEven)
        {
            if (TotalHours <= 0m) return (new(), "Total hours must be greater than 0.");
            if (TotalHours != Math.Round(TotalHours, 1, MidpointRounding.AwayFromZero))
                return (new(), "Total hours may have at most 1 decimal place.");
            var totalTenths = (int)Math.Round(TotalHours * 10m, MidpointRounding.AwayFromZero);
            var b = totalTenths / n;
            var r = totalTenths % n;
            perTaskTenths = Enumerable.Range(0, n).Select(i => b + (i < r ? 1 : 0)).ToArray();
        }

        var plan = new List<SmartFillTask>();
        for (var i = 0; i < n; i++)
        {
            var cells = new List<CellAssignment>();
            if (Mode == SmartInputMode.DistributeEven)
            {
                var tt = perTaskTenths![i];
                var db = tt / days.Count;
                var dr = tt % days.Count;
                for (var j = 0; j < days.Count; j++)
                {
                    var tenths = db + (j == days.Count - 1 ? dr : 0); // remainder on the last working day
                    if (tenths > 0) cells.Add(new CellAssignment(days[j], tenths / 10m));
                }
            }
            else // Full 8h: each working day fills to 8h, split equally across the checked tasks
            {
                var perDay = DayCapTenths / n + (i < DayCapTenths % n ? 1 : 0);
                if (perDay > 0)
                    foreach (var d in days) cells.Add(new CellAssignment(d, perDay / 10m));
            }
            if (cells.Count > 0) plan.Add(new SmartFillTask(selected[i].TaskId, cells));
        }

        return plan.Count == 0 ? (new(), "Nothing to distribute.") : (plan, null);
    }

    private static List<DateOnly> WorkingDays(DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                days.Add(d);
        return days;
    }
}
