namespace TimesheetApp.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

public sealed partial class EditableTaskRowVm : ObservableObject
{
    // 0 => brand-new task (INSERT). >0 => existing TaskItem (used for soft-delete on remove).
    public int ExistingTaskId { get; init; }

    [ObservableProperty] private string _taskName = string.Empty;
    [ObservableProperty] private int _orderIndex;
    [ObservableProperty] private bool _isRemoved;

    public static EditableTaskRowVm New(string name, int orderIndex) =>
        new() { ExistingTaskId = 0, TaskName = name, OrderIndex = orderIndex, IsRemoved = false };

    public static EditableTaskRowVm Existing(int taskId, string name, int orderIndex) =>
        new() { ExistingTaskId = taskId, TaskName = name, OrderIndex = orderIndex, IsRemoved = false };
}
