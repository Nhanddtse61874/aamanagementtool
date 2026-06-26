using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

namespace TimesheetApp.Views.Tabs;

public partial class TimesheetTab : UserControl
{
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
}
