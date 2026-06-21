namespace TimesheetApp.Views.Tabs;

using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;

public partial class SettingsTab : UserControl
{
    public SettingsTab() => InitializeComponent();

    // Dialog ownership is a View concern — services stay WPF-free (spec §4).
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFileDialog
        {
            Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = false
        };
        if (dlg.ShowDialog() == true)
            vm.DbPath = dlg.FileName;
    }
}
