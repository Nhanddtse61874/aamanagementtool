using System.Windows;
using TimesheetApp.Models;

namespace TimesheetApp.Views.Dialogs;

// Modal form for adding an issue (+ optional solution + status) to a standup entry.
public partial class StandupIssueDialog : Window
{
    public string IssueText { get; private set; } = "";
    public string? SolutionText { get; private set; }
    public string Status { get; private set; } = StandupIssueStatus.All[0];

    public StandupIssueDialog()
    {
        InitializeComponent();
        StatusBox.ItemsSource = StandupIssueStatus.All;
        StatusBox.SelectedItem = StandupIssueStatus.All[0];
        Loaded += (_, _) => IssueBox.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var text = IssueBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        IssueText = text;
        SolutionText = string.IsNullOrWhiteSpace(SolutionBox.Text) ? null : SolutionBox.Text.Trim();
        Status = StatusBox.SelectedItem as string ?? StandupIssueStatus.All[0];
        DialogResult = true;
    }
}
