using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.ViewModels;

/// One collapsible request group on the Timesheet tab: the request header (code + project), its
/// editable task rows, and an inline "add task" affordance so an EMPTY request is still loggable.
/// Adding a task inserts a Task under this request then asks the owner VM to reload + broadcast.
public sealed partial class RequestGroupVm : ObservableObject
{
    private readonly ITaskRepository _tasks;
    private readonly Func<Task> _onTaskAdded;

    public RequestGroupVm(
        int requestId, string requestCode, string project,
        ITaskRepository tasks, Func<Task> onTaskAdded,
        string? periodMonth = null, string? status = null)
    {
        RequestId = requestId;
        RequestCode = requestCode;
        Project = project;
        _tasks = tasks;
        _onTaskAdded = onTaskAdded;
        PeriodMonth = periodMonth;
        Status = status;
    }

    public int RequestId { get; }
    public string RequestCode { get; }
    public string Project { get; }
    public string? PeriodMonth { get; }   // "yyyy-MM" the ticket belongs to (v2)
    public string? Status { get; }         // ticket status (v2)

    // True for real tickets (not the hidden DEFAULT) — only these can be moved to another month.
    public bool CanMoveMonth => !string.Equals(RequestCode, "DEFAULT", StringComparison.Ordinal);

    /// Header label shown on the Expander, e.g. "REQ-8739 — Arcs 5.0".
    public string Header =>
        string.IsNullOrWhiteSpace(Project) ? RequestCode : $"{RequestCode} — {Project}";

    public ObservableCollection<TimesheetRowVm> Tasks { get; } = new();

    /// Sum of this request's task-row totals — shown on the group header chip.
    public decimal GroupTotal => Tasks.Sum(t => t.RowTotal);

    /// Owner VM calls this after recomputing totals so the header chip refreshes live.
    public void RefreshTotal() => OnPropertyChanged(nameof(GroupTotal));

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _newTaskName = "";

    [RelayCommand]
    private async Task AddTaskAsync()
    {
        var name = NewTaskName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await _tasks.InsertAsync(new TaskItem(0, RequestId, name, Tasks.Count, true));
        NewTaskName = "";
        await _onTaskAdded();   // owner reloads the grid + broadcasts DataChangedMessage
    }
}
