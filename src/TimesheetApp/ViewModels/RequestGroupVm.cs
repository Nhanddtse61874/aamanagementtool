using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

namespace TimesheetApp.ViewModels;

/// One collapsible backlog group on the Timesheet tab: the backlog header (code + project), its
/// editable task rows, and an inline "add task" affordance so an EMPTY backlog is still loggable.
/// Adding a task inserts a Task under this backlog then asks the owner VM to reload + broadcast.
public sealed partial class BacklogGroupVm : ObservableObject
{
    private readonly ITaskRepository _tasks;
    private readonly Func<Task> _onTaskAdded;

    public BacklogGroupVm(
        int backlogId, string backlogCode, string project,
        ITaskRepository tasks, Func<Task> onTaskAdded,
        string? periodMonth = null, string? type = null, string? assigneeName = null)
    {
        BacklogId = backlogId;
        BacklogCode = backlogCode;
        Project = project;
        _tasks = tasks;
        _onTaskAdded = onTaskAdded;
        PeriodMonth = periodMonth;
        Type = type;
        AssigneeName = assigneeName;
    }

    public int BacklogId { get; }
    public string BacklogCode { get; }
    public string Project { get; }
    public string? PeriodMonth { get; }    // "yyyy-MM" the ticket belongs to (v2)
    public string? Type { get; }           // ticket type (v2, formerly "status")
    public string? AssigneeName { get; }   // v4: who the ticket is assigned to (null = unassigned)

    // True for real tickets (not the hidden DEFAULT) — only these can be moved to another month.
    public bool CanMoveMonth => !string.Equals(BacklogCode, "DEFAULT", StringComparison.Ordinal);

    /// Header label shown on the Expander, e.g. "REQ-8739 — Arcs 5.0".
    public string Header =>
        string.IsNullOrWhiteSpace(Project) ? BacklogCode : $"{BacklogCode} — {Project}";

    public ObservableCollection<TimesheetRowVm> Tasks { get; } = new();

    /// Sum of this backlog's task-row totals — shown on the group header chip.
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

        await _tasks.InsertAsync(new TaskItem(0, BacklogId, name, Tasks.Count, true));
        NewTaskName = "";
        await _onTaskAdded();   // owner reloads the grid + broadcasts DataChangedMessage
    }
}
