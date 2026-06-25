namespace TimesheetApp.Views.Tabs;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TimesheetApp.ViewModels;

public partial class ReportsTab : UserControl
{
    public ReportsTab() => InitializeComponent();

    // EXP-01: dialog ownership is a View concern. Ask the VM for the .xlsx bytes, then save them.
    private async void OnExportExcel(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReportsViewModel vm) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = vm.SuggestedExportFileName(),
            DefaultExt = ".xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        var task = vm.BuildExcelExportAsync();
        if (task is null) return; // no export service wired
        try
        {
            var bytes = await task;
            File.WriteAllBytes(dlg.FileName, bytes);
            MessageBox.Show($"Exported to:\n{dlg.FileName}", "Export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Export failed: {ex.Message}", "Export",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
