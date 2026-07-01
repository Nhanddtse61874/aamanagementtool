namespace TimesheetApp.Views.Controls;

using System.Windows;

/// <summary>Small themed modal that prompts for an optional reason when a Task List deadline
/// (internal or external) is changed. The reason may be empty — OK is always enabled.
/// Returns the (possibly empty) trimmed reason via <see cref="Reason"/> when the user confirms.</summary>
public partial class DeadlineNoteDialog : Window
{
    /// <summary>The reason text entered by the user. May be null or empty when the user left the
    /// field blank and clicked OK. Always null when DialogResult is false (cancelled).</summary>
    public string? Reason { get; private set; }

    public DeadlineNoteDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ReasonBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Reason = ReasonBox.Text?.Trim();
        DialogResult = true;
    }
}
