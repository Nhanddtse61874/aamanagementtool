namespace TimesheetApp.Views.Tabs;

using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TimesheetApp.ViewModels;

public partial class TaskListTab : UserControl
{
    public TaskListTab() => InitializeComponent();

    // Grid/Gantt is a two-button mutual-exclusive toggle over the VM's single IsGantt flag.
    // ToggleButtons can be un-clicked, so re-pin IsChecked each time and keep GridToggle in sync.
    private void OnSelectGrid(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TaskListViewModel vm) vm.IsGantt = false;
        SyncToggles();
    }

    private void OnSelectGantt(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TaskListViewModel vm) vm.IsGantt = true;
        SyncToggles();
    }

    private void SyncToggles()
    {
        if (DataContext is not TaskListViewModel vm) return;
        GridToggle.IsChecked = !vm.IsGantt;
        GanttToggle.IsChecked = vm.IsGantt;
    }
}
