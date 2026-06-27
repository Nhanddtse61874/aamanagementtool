using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

namespace TimesheetApp.Views.Tabs;

public partial class DailyInputTab : UserControl
{
    private const string EntryFormat = "standupEntry";
    private Point _entryDragStart;
    private StandupEntryRowVm? _entryDrag;

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

    // ---- Drag & drop: reorder entries (⠿ grip) / drop on the trash to delete ----

    private void OnEntryDragHandleDown(object sender, MouseButtonEventArgs e)
    {
        _entryDragStart = e.GetPosition(null);
        _entryDrag = (sender as FrameworkElement)?.DataContext as StandupEntryRowVm;
    }

    private void OnEntryDragHandleMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _entryDrag is null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _entryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _entryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _entryDrag;
        _entryDrag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(EntryFormat, item), DragDropEffects.Move);
    }

    private void OnEntryDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(EntryFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnEntryDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not DailyReportViewModel vm) return;
        if (e.Data.GetData(EntryFormat) is not StandupEntryRowVm dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not StandupEntryRowVm target) return;
        if (ReferenceEquals(dragged, target)) return;
        e.Handled = true;
        await vm.ReorderEntryAsync(dragged.Model.Id, target.Model.Id);
    }

    private void OnEntryTrashDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(EntryFormat))
        {
            e.Effects = DragDropEffects.Move;
            EntryTrash.BorderThickness = new Thickness(2);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEntryTrashDragLeave(object sender, DragEventArgs e) =>
        EntryTrash.BorderThickness = new Thickness(1);

    private async void OnEntryTrashDrop(object sender, DragEventArgs e)
    {
        EntryTrash.BorderThickness = new Thickness(1);
        if (DataContext is not DailyReportViewModel vm) return;
        if (e.Data.GetData(EntryFormat) is not StandupEntryRowVm dragged) return;
        e.Handled = true;
        await vm.DeleteEntryAsync(dragged.Model.Id);
    }
}
