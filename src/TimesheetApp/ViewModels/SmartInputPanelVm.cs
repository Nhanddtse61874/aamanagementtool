// src/TimesheetApp/ViewModels/SmartInputPanelVm.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.ViewModels;

public enum SmartInputMode { DistributeEven, FillFull8h }

/// Smart Input panel (SI-06): two modes + preview. Apply overwrites cells atomically,
/// gated on a post-merge 8h validation done during preview (SI-05).
public sealed partial class SmartInputPanelVm : ObservableObject
{
    private readonly ISmartInputService _smartInput;
    private readonly ITimeLogService _timeLogs;
    private readonly Func<int> _currentUserId;

    public SmartInputPanelVm(ISmartInputService smartInput, ITimeLogService timeLogs, Func<int> currentUserId)
    {
        _smartInput = smartInput;
        _timeLogs = timeLogs;
        _currentUserId = currentUserId;
    }

    [ObservableProperty] private SmartInputMode _mode = SmartInputMode.DistributeEven;
    [ObservableProperty] private int _taskId;
    [ObservableProperty] private DateOnly _from;
    [ObservableProperty] private DateOnly _to;
    [ObservableProperty] private decimal _totalHours;
    [ObservableProperty] private string? _previewError;

    public ObservableCollection<CellAssignment> PreviewCells { get; } = new();

    /// True only after a preview that produced cells AND passed the 8h day-total validation.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    /// Raised after a successful atomic apply; owner VM reloads the week grid.
    public event Action? Applied;

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        CanApply = false;
        PreviewCells.Clear();
        PreviewError = null;

        var math = Mode == SmartInputMode.DistributeEven
            ? _smartInput.DistributeEven(From, To, TotalHours)
            : _smartInput.FillFull8h(From, To);

        if (!math.Ok)
        {
            PreviewError = math.Error;          // SI-03 no-op message
            return;
        }

        var validation = await _timeLogs.ValidateDayTotalsAsync(_currentUserId(), math.Cells, TaskId);
        foreach (var c in math.Cells) PreviewCells.Add(c);

        if (!validation.Ok)
        {
            PreviewError = validation.Error;    // SI-05 post-merge >8h block
            return;
        }
        CanApply = true;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        // Guard the path even when ExecuteAsync is invoked directly (bypasses CanExecute):
        // never write without a validated preview (SI-05).
        if (!CanApply) return;

        var result = await _timeLogs.ApplySmartInputAsync(_currentUserId(), TaskId, PreviewCells.ToList());
        if (!result.Ok)
        {
            PreviewError = result.Error;
            return;
        }
        CanApply = false;
        PreviewCells.Clear();
        Applied?.Invoke();
    }
}
