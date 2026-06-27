using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;

namespace TimesheetApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Timesheet sub-tab switch (Entry / Backlog / Reports). Maps the sub-tab index to the shell's
    // existing index-based reload (0 Timesheet, 1 Backlog, 3 Reports) so a change made elsewhere
    // shows when the sub-tab is activated. TabControl.SelectionChanged also bubbles up from inner
    // selectors (ComboBox/DataGrid/ListBox), so only react when a TabItem is the newly-selected item.
    private async void OnSubTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem) return; // ignore inner selectors
        if (DataContext is not MainViewModel vm) return;

        var index = tc.SelectedIndex switch { 0 => 0, 1 => 1, 2 => 3, _ => -1 };
        if (index >= 0) await vm.ActivateTabAsync(index);
    }
}
