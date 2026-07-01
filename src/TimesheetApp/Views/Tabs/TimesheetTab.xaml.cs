using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

namespace TimesheetApp.Views.Tabs;

public partial class TimesheetTab : UserControl
{
    private const string TaskFormat = "timesheetTask";
    private Point _taskDragStart;
    private TimesheetRowVm? _taskDrag;

    public TimesheetTab() => InitializeComponent();

    // Open the Smart Input dialog bound to the TimesheetViewModel's SmartInput panel VM (SI-06).
    // The VM raises Applied on success, which reloads the grid (wired in TimesheetViewModel ctor).
    private void OnSmartFill(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TimesheetViewModel vm) return;

        // Default the date range to the week currently shown so the user usually only checks tasks.
        vm.SmartInput.From = vm.CurrentWeek;
        vm.SmartInput.To = vm.CurrentWeek.AddDays(4);

        var dialog = new SmartInputPreviewDialog
        {
            DataContext = vm.SmartInput,
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    // Per-group "Add task": open the dedicated input dialog, then add to that backlog group.
    private async void OnAddTask(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BacklogGroupVm group }) return;
        var dlg = new TaskInputDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.TaskName is { } name)
        {
            group.NewTaskName = name;
            await group.AddTaskCommand.ExecuteAsync(null);
        }
    }

    // ---- Drag & drop: reorder task rows within a backlog (⠿ grip) / drop on the trash to delete ----

    private void OnTaskDragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _taskDragStart = e.GetPosition(null);
        _taskDrag = (sender as FrameworkElement)?.DataContext as TimesheetRowVm;
    }

    private void OnTaskDragHandleMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _taskDrag is null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _taskDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _taskDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _taskDrag;
        _taskDrag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TaskFormat, item), DragDropEffects.Move);
    }

    private void OnTaskDragOver(object sender, DragEventArgs e)
    {
        // Reorder is same-group only; show Move only when the drop would actually do something, else
        // None — a Move cursor over another group would promise a reorder the drop silently swallows.
        var sameGroup = e.Data.GetDataPresent(TaskFormat)
            && e.Data.GetData(TaskFormat) is TimesheetRowVm dragged
            && (sender as FrameworkElement)?.DataContext is TimesheetRowVm target
            && DataContext is TimesheetViewModel vm
            && vm.AreInSameGroup(dragged.TaskId, target.TaskId);
        e.Effects = sameGroup ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTaskDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not TimesheetViewModel vm) return;
        if (e.Data.GetData(TaskFormat) is not TimesheetRowVm dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not TimesheetRowVm target) return;
        if (ReferenceEquals(dragged, target)) return;
        e.Handled = true;
        await vm.ReorderTaskAsync(dragged.TaskId, target.TaskId);
    }

    private void OnTaskTrashDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TaskFormat))
        {
            e.Effects = DragDropEffects.Move;
            TaskTrash.BorderThickness = new Thickness(2);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTaskTrashDragLeave(object sender, DragEventArgs e) =>
        TaskTrash.BorderThickness = new Thickness(1);

    private async void OnTaskTrashDrop(object sender, DragEventArgs e)
    {
        TaskTrash.BorderThickness = new Thickness(1);
        if (DataContext is not TimesheetViewModel vm) return;
        if (e.Data.GetData(TaskFormat) is not TimesheetRowVm dragged) return;
        e.Handled = true;
        await vm.DeleteTaskAsync(dragged.TaskId);
    }
}
