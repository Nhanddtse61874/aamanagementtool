namespace TimesheetApp.Views.Dialogs;

using System.Windows;

/// <summary>Small themed modal that prompts for a task name (replaces the inline "type + Add" row).
/// Returns the trimmed name via <see cref="TaskName"/> when the user confirms.</summary>
public partial class TaskInputDialog : Window
{
    public string? TaskName { get; private set; }

    public TaskInputDialog(string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title)) HeaderText.Text = title;
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        TaskName = name;
        DialogResult = true;
    }
}
