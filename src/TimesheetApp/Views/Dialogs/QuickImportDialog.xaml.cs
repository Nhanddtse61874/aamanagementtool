using System;
using System.Windows;

namespace TimesheetApp.Views.Dialogs;

// P18 Quick Import: modal picker for the source day. Returns the chosen day via SelectedDate; DialogResult
// is true only when a day was picked. WPF-only — the caller clones via DailyReportViewModel.QuickImportAsync.
public partial class QuickImportDialog : Window
{
    public QuickImportDialog()
    {
        InitializeComponent();
        SourcePicker.SelectedDate = DateTime.Today.AddDays(-1);   // default: yesterday
    }

    public DateOnly SelectedDate { get; private set; }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (SourcePicker.SelectedDate is not { } dt)
        {
            ErrorText.Text = "Pick a day to import from.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        SelectedDate = DateOnly.FromDateTime(dt);
        DialogResult = true;
    }
}
