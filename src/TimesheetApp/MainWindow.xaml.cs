using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;

namespace TimesheetApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Reload the activated tab so changes from other tabs show (e.g. a new task in the grid).
    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl.SelectionChanged bubbles up from inner Selectors (ComboBox/DataGrid/ListBox);
        // only react to the tab strip's own selection.
        if (e.OriginalSource is not TabControl tc) return;
        if (DataContext is MainViewModel vm)
        {
            await vm.ActivateTabAsync(tc.SelectedIndex);
        }
    }
}
