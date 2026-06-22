using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;

namespace TimesheetApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Reload the activated tab so changes from other tabs show (defense-in-depth on top of the
    // messenger live-sync). TabControl.SelectionChanged also bubbles up from inner Selectors
    // (ComboBox/DataGrid/ListBox), so only react when a TabItem is the newly-selected item.
    private async void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tc) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem) return; // ignore inner selectors
        if (DataContext is MainViewModel vm)
        {
            await vm.ActivateTabAsync(tc.SelectedIndex);
        }
    }
}
