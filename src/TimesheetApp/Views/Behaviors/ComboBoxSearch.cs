namespace TimesheetApp.Views.Behaviors;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>
/// Attached behavior that turns a ComboBox into a type-to-filter picker: set
/// <c>beh:ComboBoxSearch.Enabled="True"</c>. The combo becomes editable; typing narrows the dropdown to
/// items whose display text contains the query (case-insensitive). Selecting an item, clearing the text,
/// or reopening the dropdown restores the full list.
///
/// Pair it with <c>DisplayMemberPath</c> (dropdown rows) and <c>TextSearch.TextPath</c> (the editable text
/// of the selected item) pointing at the same property, e.g. both "Name".
/// </summary>
public static class ComboBoxSearch
{
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ComboBoxSearch),
            new PropertyMetadata(false, OnEnabledChanged));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox cb) return;
        if ((bool)e.NewValue)
        {
            cb.IsEditable = true;
            cb.IsTextSearchEnabled = false;   // we do the filtering; don't let WPF auto-jump/auto-complete
            cb.StaysOpenOnEdit = true;
            cb.Loaded += OnLoaded;
            cb.DropDownOpened += OnDropDownOpened;
            cb.DropDownClosed += OnDropDownClosed;
        }
        else
        {
            cb.Loaded -= OnLoaded;
            cb.DropDownOpened -= OnDropDownOpened;
            cb.DropDownClosed -= OnDropDownClosed;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cb = (ComboBox)sender;
        // Editable text of the selected item: fall back to DisplayMemberPath when TextPath wasn't set,
        // otherwise the editable box would show the item's type name instead of its display value.
        if (string.IsNullOrEmpty(TextSearch.GetTextPath(cb)) && !string.IsNullOrEmpty(cb.DisplayMemberPath))
            TextSearch.SetTextPath(cb, cb.DisplayMemberPath);

        if (cb.Template?.FindName("PART_EditableTextBox", cb) is TextBox tb)
        {
            tb.TextChanged -= OnTextChanged;
            tb.TextChanged += OnTextChanged;
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        if (FindParentComboBox(tb) is not { } cb) return;

        // Ignore programmatic text updates (e.g. the echo when SelectedItem is assigned) — only react to
        // the user actually typing into the focused box. Otherwise selecting an item would re-open + filter.
        if (!tb.IsKeyboardFocusWithin) return;
        if (string.Equals(Display(cb, cb.SelectedItem), tb.Text, StringComparison.Ordinal)) return;

        ApplyFilter(cb, tb.Text);
        if (!string.IsNullOrEmpty(tb.Text) && !cb.IsDropDownOpen)
            cb.IsDropDownOpen = true;
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        // Always start from the full, unfiltered list when the dropdown opens.
        if (sender is ComboBox cb) SetFilter(cb, null);
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        // Clear the filter so the (possibly shared) collection view is unfiltered for everyone else.
        if (sender is ComboBox cb) SetFilter(cb, null);
    }

    private static void ApplyFilter(ComboBox cb, string? text) =>
        SetFilter(cb, string.IsNullOrWhiteSpace(text) ? null : text!.Trim());

    private static void SetFilter(ComboBox cb, string? query)
    {
        if (CollectionViewSource.GetDefaultView(cb.ItemsSource) is not { } view) return;
        if (query is null)
            view.Filter = null;
        else
            view.Filter = item => Display(cb, item).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string Display(ComboBox cb, object? item)
    {
        if (item is null) return string.Empty;
        var path = cb.DisplayMemberPath;
        if (!string.IsNullOrEmpty(path))
            return item.GetType().GetProperty(path)?.GetValue(item)?.ToString() ?? string.Empty;
        return item.ToString() ?? string.Empty;
    }

    private static ComboBox? FindParentComboBox(DependencyObject? d)
    {
        while (d is not null and not ComboBox)
            d = VisualTreeHelper.GetParent(d);
        return d as ComboBox;
    }
}
