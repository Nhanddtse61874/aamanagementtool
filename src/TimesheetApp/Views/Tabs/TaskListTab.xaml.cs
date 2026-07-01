namespace TimesheetApp.Views.Tabs;

using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TimesheetApp.Models;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Controls;

public partial class TaskListTab : UserControl
{
    // P13 W3 inline deadline edit: guard so the programmatic SelectedDate write we do when REVERTING a
    // cancelled change does not re-enter the change handler (the user-initiated gate is IsKeyboardFocusWithin
    // + a value-vs-source echo check; this flag covers the revert write unconditionally).
    private bool _suppressDeadlineChange;

    // Fixed per-working-day column width (px). The canvas grows to Axis.Count*dayWidth and scrolls when
    // it would overflow; if the viewport is wider, columns stretch to fill it (see ResolveDayWidth).
    private const double MinDayWidth = 26d;
    private const double RowHeight = 26d;
    private const double BarHeight = 16d;
    private const double HeaderHeight = 34d;
    private const double LabelGutter = 130d;   // left strip for the backlog-code labels

    public TaskListTab()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Grid/Gantt is a two-button mutual-exclusive toggle over the VM's single IsGantt flag.
    // ToggleButtons can be un-clicked, so re-pin IsChecked each time and keep GridToggle in sync.
    private void OnSelectGrid(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TaskListViewModel vm) vm.IsGantt = false;
        SyncToggles();
    }

    private void OnSelectGantt(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is TaskListViewModel vm) vm.IsGantt = true;
        SyncToggles();
    }

    private void SyncToggles()
    {
        if (DataContext is not TaskListViewModel vm) return;
        GridToggle.IsChecked = !vm.IsGantt;
        GanttToggle.IsChecked = vm.IsGantt;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TaskListViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is TaskListViewModel newVm)
            newVm.PropertyChanged += OnVmPropertyChanged;
        DrawGantt();
    }

    // Redraw when the model is rebuilt, the view toggles to Gantt, or the chart is shown again.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskListViewModel.Gantt)
            or nameof(TaskListViewModel.IsGantt)
            or nameof(TaskListViewModel.IsChartCollapsed))
            DrawGantt();
    }

    // The canvas reports its real width only after layout — redraw so dayWidth uses the live viewport (R5).
    private void OnGanttCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawGantt();

    // ---- P13 W3: inline deadline edit (DatePicker SelectedDateChanged -> reason popup -> commit) ----------

    private void OnInternalDeadlineChanged(object sender, SelectionChangedEventArgs e)
        => HandleDeadlineChanged(sender as DatePicker, isInternal: true);

    private void OnExternalDeadlineChanged(object sender, SelectionChangedEventArgs e)
        => HandleDeadlineChanged(sender as DatePicker, isInternal: false);

    // Shared deadline-change flow. Only a genuine user pick (focus is within this DatePicker, and the new
    // value actually differs from the row's current deadline) opens the reason popup. On OK -> commit with
    // the note; on Cancel -> revert the DatePicker to the prior value (guarded so the revert doesn't re-enter).
    private void HandleDeadlineChanged(DatePicker? picker, bool isInternal)
    {
        if (_suppressDeadlineChange) return;                       // ignore the revert write
        if (picker?.Tag is not TaskListRowVm row) return;
        if (DataContext is not TaskListViewModel vm) return;

        // The bound source value (current deadline on the row) — used as the "prior" value + echo check.
        var current = isInternal ? row.DeadlineInternal : row.DeadlineExternal;
        var picked = picker.SelectedDate is { } dt ? DateOnly.FromDateTime(dt) : (DateOnly?)null;

        // Programmatic set (initial bind / grid reload / revert-to-same) — not a user edit.
        if (picked == current) return;
        // User-initiated gate: the initial OneWay bind happens before the control is ever focused.
        if (!picker.IsKeyboardFocusWithin) return;

        var dlg = new DeadlineNoteDialog { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true)
        {
            _ = vm.CommitDeadlineAsync(row.BacklogId, isInternal, picked, dlg.Reason);
        }
        else
        {
            // Cancelled — put the DatePicker back to the prior value without re-triggering this handler.
            _suppressDeadlineChange = true;
            picker.SelectedDate = current is { } c ? c.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
            _suppressDeadlineChange = false;
        }
    }

    // ---- P13 (QA): inline ComboBox edits (TYPE / PCT / PCA / STATUS). Commit ONLY on a user-initiated
    //      selection (keyboard focus is within the ComboBox), mirroring the DatePicker guard above. WPF
    //      raises SelectionChanged with a spurious null/echo value while the DataGrid realizes or reloads
    //      a row (the control is unfocused then); committing on those wiped the fields to null on reopen.
    //      Handled=true stops the child event bubbling up to the DataGrid's own selection handling. ----

    private void OnRowInlineComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: true, DataContext: TaskListRowVm row })
            row.CommitInlineEdit();
        e.Handled = true;
    }

    private void OnTaskExtendedComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: true, DataContext: TaskRowVm task })
            task.CommitExtendedEdit();
        e.Handled = true;
    }

    private void OnTaskStatusComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: true, DataContext: TaskRowVm task })
            task.CommitStatusEdit();
        e.Handled = true;
    }

    // ---- P13 (QA): Progress cell inline edit. Click the % bar -> IsEditingProgress=true swaps in a 0-100
    //      number input (auto-focused). Enter or click-away commits (through the EditProgressText LostFocus
    //      binding) and swaps the bar back in; Escape cancels, restoring the committed value. ----

    private void OnProgressDisplayClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskListRowVm row)
            row.IsEditingProgress = true;   // IsVisibleChanged then focuses the input
    }

    // Focus + select the input the moment it becomes visible so the user can type immediately.
    private void OnProgressEditVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.IsVisible)
            tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }),
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnProgressEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TaskListRowVm row }) return;
        if (e.Key == Key.Enter)
        {
            // Collapsing the box drops focus → the LostFocus-triggered binding commits EditProgressText.
            row.IsEditingProgress = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.ResetProgressEdit();          // restore the committed value (no persist)
            row.IsEditingProgress = false;
            e.Handled = true;
        }
    }

    private void OnProgressEditLostFocus(object sender, RoutedEventArgs e)
    {
        // Click-away: the LostFocus-triggered binding already pushed the value; swap the bar back in.
        if ((sender as FrameworkElement)?.DataContext is TaskListRowVm row)
            row.IsEditingProgress = false;
    }

    private static Brush Res(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private void DrawGantt()
    {
        if (GanttCanvas is null) return;
        GanttCanvas.Children.Clear();

        if (DataContext is not TaskListViewModel vm || !vm.IsGantt || vm.IsChartCollapsed) return;
        var model = vm.Gantt;
        if (model is null || model.Axis.Count == 0)
        {
            DrawEmptyHint();
            return;
        }

        // Theme brushes (fall back to literals if the theme dictionary is not loaded, e.g. design-time).
        var accent = Res("Accent", new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E)));
        var danger = Res("Danger", new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
        var amber = Res("AmberFg", new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)));
        var border = Res("Border", new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xEE)));
        var headerBg = Res("HeaderBg", new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
        var textSec = Res("TextSecondary", new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
        var textPri = Res("TextPrimary", new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));

        var axis = model.Axis;
        var dayWidth = ResolveDayWidth(axis.Count);
        var plotWidth = axis.Count * dayWidth;
        var totalWidth = LabelGutter + plotWidth;
        var totalHeight = HeaderHeight + model.Bars.Count * RowHeight + 6;

        GanttCanvas.Width = totalWidth;
        GanttCanvas.Height = totalHeight;

        // --- Column grid + day/date axis labels (working days only — weekends/holidays are not on the axis).
        for (var i = 0; i < axis.Count; i++)
        {
            var x = LabelGutter + i * dayWidth;

            // faint column separator
            GanttCanvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = totalHeight,
                Stroke = border, StrokeThickness = 0.7
            });

            var day = axis[i];
            // Date label: day-of-month on top, weekday abbreviation underneath (kept terse).
            var label = new TextBlock
            {
                Text = day.Day.ToString("00", CultureInfo.InvariantCulture),
                FontSize = 11, Foreground = textPri, TextAlignment = TextAlignment.Center,
                Width = dayWidth
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, 2);
            GanttCanvas.Children.Add(label);

            var weekday = new TextBlock
            {
                Text = day.ToString("ddd", CultureInfo.InvariantCulture),
                FontSize = 9, Foreground = textSec, TextAlignment = TextAlignment.Center,
                Width = dayWidth
            };
            Canvas.SetLeft(weekday, x);
            Canvas.SetTop(weekday, 16);
            GanttCanvas.Children.Add(weekday);
        }

        // header baseline
        GanttCanvas.Children.Add(new Line
        {
            X1 = 0, X2 = totalWidth, Y1 = HeaderHeight, Y2 = HeaderHeight,
            Stroke = border, StrokeThickness = 1
        });

        // --- One row per bar.
        for (var r = 0; r < model.Bars.Count; r++)
        {
            var bar = model.Bars[r];
            var rowTop = HeaderHeight + r * RowHeight;
            var barTop = rowTop + (RowHeight - BarHeight) / 2;

            // Backlog-code label in the left gutter.
            var codeLabel = new TextBlock
            {
                Text = bar.BacklogCode, FontSize = 11, Foreground = textPri,
                TextTrimming = TextTrimming.CharacterEllipsis, Width = LabelGutter - 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(codeLabel, 4);
            Canvas.SetTop(codeLabel, barTop);
            GanttCanvas.Children.Add(codeLabel);

            var barColor = bar.ScheduleState switch
            {
                ScheduleState.Late => danger,
                ScheduleState.Warning => amber,
                _ => accent
            };

            if (bar.HasStart && bar.SpanWorkingDays > 0)
            {
                var rect = new Rectangle
                {
                    Width = System.Math.Max(dayWidth, bar.SpanWorkingDays * dayWidth),
                    Height = BarHeight, RadiusX = 4, RadiusY = 4, Fill = barColor
                };
                Canvas.SetLeft(rect, LabelGutter + bar.StartDayIndex * dayWidth);
                Canvas.SetTop(rect, barTop);
                GanttCanvas.Children.Add(rect);
            }
            else
            {
                // No start_date → a faint dashed placeholder row spanning the plot, plus the deadline marker.
                var placeholder = new Rectangle
                {
                    Width = plotWidth, Height = BarHeight, RadiusX = 4, RadiusY = 4,
                    Stroke = textSec, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 },
                    Fill = Brushes.Transparent, Opacity = 0.55
                };
                Canvas.SetLeft(placeholder, LabelGutter);
                Canvas.SetTop(placeholder, barTop);
                GanttCanvas.Children.Add(placeholder);
            }

            // External-deadline marker (PCA): a small downward triangle at the marker column.
            if (bar.ExternalMarkerIndex is { } extIdx)
            {
                var cx = LabelGutter + extIdx * dayWidth + dayWidth / 2;
                var marker = new Polygon
                {
                    Points = new PointCollection
                    {
                        new Point(cx - 5, rowTop + 2),
                        new Point(cx + 5, rowTop + 2),
                        new Point(cx, rowTop + 10)
                    },
                    Fill = danger,
                    ToolTip = "PCA (external) deadline"
                };
                GanttCanvas.Children.Add(marker);

                var line = new Line
                {
                    X1 = cx, X2 = cx, Y1 = rowTop + 2, Y2 = rowTop + RowHeight - 2,
                    Stroke = danger, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }
                };
                GanttCanvas.Children.Add(line);
            }
        }
    }

    // Working-day column width: fixed minimum so the chart scrolls on busy months; if the viewport is
    // wider than the minimum layout, stretch columns to fill it. ActualWidth is read post-layout (R5);
    // 0 (pre-layout) falls back to the minimum so the first paint is still sensible.
    private double ResolveDayWidth(int axisCount)
    {
        if (axisCount <= 0) return MinDayWidth;
        var viewport = GanttCanvas?.ActualWidth ?? 0;
        var available = viewport - LabelGutter;
        if (available <= 0) return MinDayWidth;
        var stretched = available / axisCount;
        return stretched > MinDayWidth ? stretched : MinDayWidth;
    }

    private void DrawEmptyHint()
    {
        var textSec = Res("TextSecondary", new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
        var hint = new TextBlock
        {
            Text = "No dated backlogs to chart for this month.",
            Foreground = textSec, FontSize = 12
        };
        Canvas.SetLeft(hint, 8);
        Canvas.SetTop(hint, 8);
        GanttCanvas.Children.Add(hint);
    }
}
