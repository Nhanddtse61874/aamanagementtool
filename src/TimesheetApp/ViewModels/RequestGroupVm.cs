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
        ITaskRepository tasks, Func<Task> onTaskAdded)
    {
        RequestId = requestId;
        RequestCode = requestCode;
        Project = project;
        _tasks = tasks;
        _onTaskAdded = onTaskAdded;
    }

    public int RequestId { get; }
    public string RequestCode { get; }
    public string Project { get; }

    /// Header label shown on the Expander, e.g. "REQ-8739 — Arcs 5.0".
    public string Header =>
        string.IsNullOrWhiteSpace(Project) ? RequestCode : $"{RequestCode} — {Project}";

    public ObservableCollection<TimesheetRowVm> Tasks { get; } = new();

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
