namespace TimesheetApp.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;

// One editable task-name row inside the template editor. Templates have no per-row identity to
// preserve on edit (edit = delete-then-reinsert all rows), so unlike EditableTaskRowVm this carries
// no existing-id / soft-delete state — just the name and its display order.
public sealed partial class TemplateTaskRowVm : ObservableObject
{
    [ObservableProperty] private string _taskName = string.Empty;
    [ObservableProperty] private int _orderIndex;

    public static TemplateTaskRowVm New(string name, int orderIndex) =>
        new() { TaskName = name, OrderIndex = orderIndex };
}
