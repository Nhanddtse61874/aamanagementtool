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
        var dialog = new SmartInputPreviewDialog
        {
            DataContext = vm.SmartInput,
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
