using System.Windows;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Converters;
using TimesheetApp.Views.Tabs;
using Xunit;

namespace TimesheetApp.Tests.Views;

// P13 Wave 3 regression guard: the Task List grid + expand sub-rows became inline-editable (Type/PCT/PCA
// dropdowns, deadline DatePickers, a TagPicker, an editable Progress box, and per-task editors). The WPF
// render-crash class (a TwoWay binding to a read-only VM prop, or a Button-typed Style on a ToggleButton)
// only throws when the cell/sub-row TEMPLATE actually renders with a row present — invisible to unit tests.
// This renders the real TaskListTab, with one seeded backlog + task and its row expanded, on an STA thread,
// so any such regression fails CI instead of surfacing as the runtime error dialog.
// P16: the grid is now an ItemsControl of per-backlog cards (tag strip on top + expandable detail); this
// guard still renders a seeded, expanded card so the card template / TwoWay combos stay render-safe.
[Collection("WpfSta")]
public sealed class TaskListTabRenderTests
{
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    // Mirror App.xaml: merge Theme.xaml + register every converter the TaskListTab XAML references so each
    // StaticResource resolves during instantiation/layout.
    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application();
        if (!app.Resources.Contains("ToolbarGhostToggle"))
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Light.xaml", UriKind.Absolute) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/TimesheetApp;component/Views/Theme/Theme.xaml", UriKind.Absolute),
            });
        }
        app.Resources["NullToCollapsedConverter"] = new NullToCollapsedConverter();
        app.Resources["BoolToVisibleConverter"] = new BoolToVisibilityConverter();
        app.Resources["HexToBrush"] = new HexToBrushConverter();
        app.Resources["DateOnly"] = new DateOnlyConverter();
    }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private sealed class StubArchive : ITaskListArchiveService
    {
        public string FileNameFor(int year, int month) => $"{year:0000}{month:00}_tasklist.md";
        public Task<string?> ExportMonthAsync(int year, int month) => Task.FromResult<string?>(null);
        public Task<string?> BuildMonthMarkdownAsync(IReadOnlyList<int>? teamIds, int year, int month) => Task.FromResult<string?>(null);
        public Task BackfillMissingMonthsAsync() => Task.CompletedTask;
    }

    [Fact]
    public void TaskListTab_renders_with_editable_row_and_expanded_subrows_without_crash()
    {
        RunSta(() =>
        {
            EnsureAppResources();

            var db = TestDb.CreateAsync().GetAwaiter().GetResult();
            var backlogs = new BacklogRepository(db);
            var tasks = new TaskRepository(db);
            var tagsRepo = new TagRepository(db);

            // One non-DEFAULT backlog in the clock's month (so it shows under the default month selector),
            // a task (so the expanded sub-row editors render), and a tag (so the TagPicker has content).
            var bid = backlogs.InsertAsync(new Backlog(
                0, "REQ-RENDER", "ARCS", DateTimeOffset.UtcNow,
                PeriodMonth: "2026-06", ProgressPercent: 40,
                DeadlineInternal: new DateOnly(2026, 6, 20))).GetAwaiter().GetResult();
            tasks.InsertAsync(new TaskItem(0, bid, "Task A", 0, true)).GetAwaiter().GetResult();
            var tagId = tagsRepo.InsertAsync(new Tag(0, "Urgent", "⚡", "#DC2626", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
            backlogs.SetTagsAsync(bid, new[] { tagId }).GetAwaiter().GetResult();

            var vm = new TaskListViewModel(
                backlogs, tasks, new TimeLogRepository(db), tagsRepo,
                new PcaContactRepository(db), new UserRepository(db), new HolidayRepository(db),
                new WorkingDayCalculator(), new ScheduleStateService(), new StubArchive(),
                new FakeClock { Today = new DateOnly(2026, 6, 15) }, messenger: null);
            vm.LoadAsync().GetAwaiter().GetResult();

            Assert.NotEmpty(vm.Rows);          // the seeded backlog produced a row
            vm.Rows[0].IsExpanded = true;      // force the editable sub-row template to render

            var win = new Window
            {
                Content = new TaskListTab { DataContext = vm },
                Width = 1200,
                Height = 720,
            };
            win.Show();
            win.UpdateLayout();   // throws here if any editable cell / sub-row binding is render-unsafe
            win.Close();

            db.Dispose();
        });
    }
}
