using System.Windows;
using System.Windows.Controls;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

namespace TimesheetApp.Views.Tabs;

public partial class DailyInputTab : UserControl
{
    public DailyInputTab() => InitializeComponent();

    // "+ Add entry" → open the entry dialog (the Yesterday/Today section is chosen inside it); persist on confirm.
    private async void OnAddEntry(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DailyReportViewModel vm) return;
        var draft = vm.NewToday;   // single draft; the dialog's section picker defaults to Today
        draft.Reset();             // start clean (resets the section back to Today)

        var dialog = new StandupEntryDialog(draft) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await vm.AddEntryAsync(draft);
    }

    // "+ Issue" → open the issue dialog for the entry under the button; persist on confirm.
    private async void OnAddIssue(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DailyReportViewModel vm) return;
        if ((sender as FrameworkElement)?.DataContext is not StandupEntryRowVm row) return;

        var dialog = new StandupIssueDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
            await vm.AddIssueAsync(row.Model.Id, dialog.IssueText, dialog.SolutionText, dialog.Status);
    }
}
