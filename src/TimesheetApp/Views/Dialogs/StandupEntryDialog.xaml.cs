using System.Windows;
using TimesheetApp.ViewModels;

namespace TimesheetApp.Views.Dialogs;

// Modal form for adding a standup entry. DataContext is the section's StandupDraftVm (already carrying
// the request/task picker state); on confirm it validates and returns true so the caller can persist.
public partial class StandupEntryDialog : Window
{
    private readonly StandupDraftVm _draft;

    public StandupEntryDialog(StandupDraftVm draft)
    {
        InitializeComponent();
        _draft = draft;
        DataContext = draft;
        HeaderText.Text = draft.Section == Models.StandupSection.Yesterday
            ? "Add — Yesterday" : "Add — Today";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_draft.RequestCode) || string.IsNullOrWhiteSpace(_draft.TaskText))
        {
            ErrorText.Text = "Request code and task are both required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
