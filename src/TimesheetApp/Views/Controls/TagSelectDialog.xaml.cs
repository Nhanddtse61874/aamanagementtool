namespace TimesheetApp.Views.Controls;

using System.Collections;
using System.Windows;

/// <summary>
/// Modal tag picker used from the Task List. A Popup opened from inside a DataGrid cell / row-details
/// closes before a checkbox can be ticked, so tag editing uses this window instead. The checkboxes bind to
/// the same TagPickVm items the row view-model is subscribed to, so each toggle commits immediately; the
/// host refreshes the chips when the dialog closes.
/// </summary>
public partial class TagSelectDialog : Window
{
    public TagSelectDialog(IEnumerable tagPicks)
    {
        InitializeComponent();
        TagList.ItemsSource = tagPicks;
    }

    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = true;
}
