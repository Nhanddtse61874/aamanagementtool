using CommunityToolkit.Mvvm.Messaging;
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class TimesheetViewModelTests
{
    // Wed 2026-06-17 -> Monday of that week is 2026-06-15.
    private static readonly DateOnly Wed = new(2026, 6, 17);
    private static readonly DateOnly Mon = new(2026, 6, 15);

    // Build the grouped shape from flat WeekRows: each distinct BacklogCode becomes one group.
    private static IReadOnlyList<WeekBacklogGroup> Groups(params WeekRow[] rows)
    {
        var id = 1;
        return rows
            .GroupBy(r => r.BacklogCode)
            .Select(g => new WeekBacklogGroup(id++, g.Key, "", g.ToList()))
            .ToList();
    }

    // Convenience: all task rows across all groups, flattened, in group order.
    private static IReadOnlyList<TimesheetRowVm> AllTasks(TimesheetViewModel vm) =>
        vm.Groups.SelectMany(g => g.Tasks).ToList();

    private static (TimesheetViewModel vm, Mock<ITimeLogService> tl, Mock<ISmartInputService> si) Make(
        IReadOnlyList<WeekBacklogGroup>? initial = null, int userId = 1)
    {
        var tl = new Mock<ITimeLogService>();
        var si = new Mock<ISmartInputService>();
        var tasks = new Mock<ITaskRepository>();
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed);

        tl.Setup(t => t.GetWeekGroupedAsync(userId, It.IsAny<DateOnly>()))
          .ReturnsAsync((int _, DateOnly _) => initial ?? System.Array.Empty<WeekBacklogGroup>());
        tl.Setup(t => t.SaveCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<decimal>()))
          .ReturnsAsync(new SaveResult(true, null));
        tl.Setup(t => t.ClearCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>()))
          .Returns(Task.CompletedTask);

        // Isolated messenger per VM: the default WeakReferenceMessenger is process-wide, so VMs from
        // other tests would otherwise stay subscribed and react to broadcasts, double-counting reloads.
        var vm = new TimesheetViewModel(
            tl.Object, tasks.Object, si.Object, clock.Object, () => userId, new WeakReferenceMessenger());
        return (vm, tl, si);
    }

    [Fact] // ISSUE 5 (HOL-02): a day marked as a holiday makes that day-column read-only in the entry grid.
    public async Task Holiday_makes_that_day_column_readonly()
    {
        var tl = new Mock<ITimeLogService>();
        tl.Setup(t => t.GetWeekGroupedAsync(It.IsAny<int>(), It.IsAny<DateOnly>()))
          .ReturnsAsync(System.Array.Empty<WeekBacklogGroup>());
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed); // week of Mon 2026-06-15

        // Mark Wednesday (2026-06-17) of the visible week as a holiday.
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetAllAsync())
                .ReturnsAsync(new[] { new Holiday(Wed, "Company holiday") });

        var vm = new TimesheetViewModel(
            tl.Object, Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), clock.Object,
            () => 1, new WeakReferenceMessenger(),
            users: null, backlogs: null, currentUser: null, settings: null, holidays: holidays.Object);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.WedIsHoliday);
        Assert.True(vm.WedReadOnly);     // holiday column is not editable
        Assert.False(vm.MonIsHoliday);   // other weekdays unaffected
        Assert.False(vm.MonReadOnly);
        Assert.False(vm.TueReadOnly);
    }

    [Fact] // v2: "Move ▶" bumps the ticket's period_month to the NEXT month, audited as the current user.
    public async Task MoveMonth_advances_ticket_to_next_month_and_audits_current_user()
    {
        var tl = new Mock<ITimeLogService>();
        tl.Setup(t => t.GetWeekGroupedAsync(It.IsAny<int>(), It.IsAny<DateOnly>()))
          .ReturnsAsync(System.Array.Empty<WeekBacklogGroup>());
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed); // selected month defaults to 2026-06

        var requests = new Mock<IBacklogRepository>();
        requests.Setup(r => r.GetByIdAsync(5))
                .ReturnsAsync(new Backlog(5, "REQ-5", "P", DateTimeOffset.UtcNow, PeriodMonth: "2026-06"));

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.Current).Returns(new User(7, "Nhan", null, true));

        var vm = new TimesheetViewModel(
            tl.Object, Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), clock.Object,
            () => 1, new WeakReferenceMessenger(), Mock.Of<IUserRepository>(), requests.Object, currentUser.Object);

        await vm.MoveMonthCommand.ExecuteAsync(5);

        // A month roll is a bump-only write: it carries no version, so it lands on UpdateAsync, not
        // UpdateCheckedAsync. (W3.5: the optional expectedVersion parameter that once forced this
        // expression tree to spell out an extra argument — CS0854 — is gone, so this is the original
        // call shape again.)
        requests.Verify(r => r.UpdateAsync(
            It.Is<Backlog>(x => x.Id == 5 && x.PeriodMonth == "2026-07"), 7, "Nhan", It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task Load_SetsCurrentWeekToMondayOfToday()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(Mon, vm.CurrentWeek);
    }

    [Fact]
    public async Task Headers_ShowConcreteDates_MonToFri()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("MON 15/06", vm.MonHeader);
        Assert.Equal("FRI 19/06", vm.FriHeader);
    }

    [Fact]
    public async Task Next_ShiftsWeekForwardSevenDays_AndReloads()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.NextWeekCommand.ExecuteAsync(null);
        Assert.Equal(Mon.AddDays(7), vm.CurrentWeek);
        tl.Verify(t => t.GetWeekGroupedAsync(1, Mon.AddDays(7)), Times.Once);
    }

    [Fact] // Jump-to-week: picking any date snaps CurrentWeek to that date's Monday and reloads.
    public async Task JumpDate_SnapsToMondayOfPickedDate_AndReloads()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        tl.Invocations.Clear();

        vm.JumpDate = new DateTime(2026, 7, 1); // Wed -> Monday 2026-06-29

        Assert.Equal(new DateOnly(2026, 6, 29), vm.CurrentWeek);
        tl.Verify(t => t.GetWeekGroupedAsync(1, new DateOnly(2026, 6, 29)), Times.Once);
    }

    [Fact] // Navigating Prev/Next keeps the jump DatePicker in sync with the visible week.
    public async Task Navigation_KeepsJumpDateInSyncWithCurrentWeek()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.NextWeekCommand.ExecuteAsync(null);

        Assert.Equal(vm.CurrentWeek, DateOnly.FromDateTime(vm.JumpDate!.Value));
    }

    [Fact]
    public async Task Prev_ShiftsWeekBackSevenDays()
    {
        var (vm, _, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.PreviousWeekCommand.ExecuteAsync(null);
        Assert.Equal(Mon.AddDays(-7), vm.CurrentWeek);
    }

    [Fact]
    public async Task Groups_ShapedFromWeek_OneGroupPerRequest_TasksUnderEach()
    {
        var groups = Groups(
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, 3m, null, null),
            new WeekRow(9, "DEFAULT", "Annual Leave", 0, null, null, null, null, 8m));
        var (vm, _, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("REQ-001", vm.Groups[0].BacklogCode);
        Assert.Equal(7, vm.Groups[0].Tasks[0].TaskId);
        Assert.Equal(4m, vm.Groups[0].Tasks[0].Mon);
        Assert.Equal("DEFAULT", vm.Groups[1].BacklogCode);
        Assert.Equal("Annual Leave", vm.Groups[1].Tasks[0].TaskName);
    }

    [Fact]
    public async Task AreInSameGroup_TrueWithinGroup_FalseAcrossGroupsOrUnknown()  // reorder cursor honesty
    {
        var groups = Groups(
            new WeekRow(7, "REQ-001", "Implement", 0, null, null, null, null, null),
            new WeekRow(8, "REQ-001", "Review", 1, null, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Annual Leave", 0, null, null, null, null, null));
        var (vm, _, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.True(vm.AreInSameGroup(7, 8));    // both under REQ-001 → reorder allowed
        Assert.False(vm.AreInSameGroup(7, 9));   // REQ-001 vs DEFAULT → cross-group, no reorder
        Assert.False(vm.AreInSameGroup(7, 404)); // unknown target id
    }

    [Fact]
    public async Task EmptyRequest_RendersAsGroupWithNoTasks()
    {
        // One request that has NO tasks -> a group with an empty Tasks list still shows.
        var groups = new[] { new WeekBacklogGroup(3, "REQ-EMPTY", "Proj", System.Array.Empty<WeekRow>()) };
        var (vm, _, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.Groups);
        Assert.Equal("REQ-EMPTY", vm.Groups[0].BacklogCode);
        Assert.Empty(vm.Groups[0].Tasks);
    }

    [Fact]
    public async Task ColumnTotals_SumAllTasksAcrossGroups_AndUpdateOnCellChange()
    {
        var groups = Groups(
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 2m, null, null, null, null));
        var (vm, _, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(6m, vm.MonTotal);

        AllTasks(vm)[0].Mon = 5m;               // 5 + 2
        Assert.Equal(7m, vm.MonTotal);
    }

    [Fact]
    public async Task Save_DisabledWhenAnyColumnExceedsEight()
    {
        var groups = Groups(
            new WeekRow(7, "REQ-001", "Implement", 0, 5m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 4m, null, null, null, null)); // Mon = 9 > 8
        var (vm, _, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.SaveCommand.CanExecute(null));

        AllTasks(vm)[1].Mon = 2m;               // Mon now 7
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact] // Editing a cell auto-saves it (no Save button): value -> upsert on the natural key.
    public async Task EditingCell_AutoSaves_UpsertsOnNaturalKey()
    {
        var groups = Groups(new WeekRow(7, "REQ-001", "Implement", 0, null, null, null, null, null));
        var (vm, tl, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        AllTasks(vm)[0].Mon = 4m;   // commit -> auto-save fires from the setter
        await vm.LastAutoSave;

        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, 4m), Times.Once);
        Assert.Equal("✓ Saved", vm.SaveStatus);
        Assert.False(vm.SaveStatusIsError);
    }

    [Fact] // Clearing a cell auto-deletes its log (empty = 0h), never upserts.
    public async Task ClearingCell_AutoDeletesLog()
    {
        var groups = Groups(new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null));
        var (vm, tl, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        AllTasks(vm)[0].Mon = null; // cleared
        await vm.LastAutoSave;

        tl.Verify(t => t.ClearCellAsync(1, 7, Mon), Times.Once);
        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, It.IsAny<decimal>()), Times.Never);
    }

    [Fact] // A red (invalid) cell is left on screen but NEVER written to storage; status flags it.
    public async Task EditingInvalidCell_DoesNotPersist_AndFlagsStatus()
    {
        var groups = Groups(new WeekRow(7, "REQ-001", "Implement", 0, null, null, null, null, null));
        var (vm, tl, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        AllTasks(vm)[0].Mon = 9m;   // > 8h single cell -> per-cell validation error
        await vm.LastAutoSave;

        tl.Verify(t => t.SaveCellAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateOnly>(),
            It.IsAny<decimal>()), Times.Never);
        Assert.True(vm.SaveStatusIsError);
    }

    [Fact] // The Entry "Collapse all" toggle is persisted and restored in a later session.
    public async Task CollapseAll_Preference_IsPersisted_AndRestored()
    {
        var tl = new Mock<ITimeLogService>();
        tl.Setup(t => t.GetWeekGroupedAsync(It.IsAny<int>(), It.IsAny<DateOnly>()))
          .ReturnsAsync(System.Array.Empty<WeekBacklogGroup>());
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed);

        var store = new Dictionary<string, string>();
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string k, string v) => { store[k] = v; return Task.CompletedTask; });
        settings.Setup(s => s.GetAsync(It.IsAny<string>()))
                .ReturnsAsync((string k) => store.TryGetValue(k, out var v) ? v : null);

        TimesheetViewModel NewVm() => new(
            tl.Object, Mock.Of<ITaskRepository>(), Mock.Of<ISmartInputService>(), clock.Object,
            () => 1, new WeakReferenceMessenger(), null, null, null, settings.Object);

        // Session 1: default expanded; toggling collapse-all on persists the preference.
        var vm1 = NewVm();
        await vm1.LoadCommand.ExecuteAsync(null);
        Assert.False(vm1.AllCollapsed);
        vm1.ToggleCollapseAllCommand.Execute(null);
        Assert.True(vm1.AllCollapsed);

        // Session 2: a fresh VM restores "collapsed" from settings on load.
        var vm2 = NewVm();
        await vm2.LoadCommand.ExecuteAsync(null);
        Assert.True(vm2.AllCollapsed);
    }

    [Fact]
    public async Task SmartInputApplied_ReloadsWeek()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        tl.Invocations.Clear();

        vm.RaiseSmartInputAppliedForTest();   // internal test hook -> ReloadAsync

        tl.Verify(t => t.GetWeekGroupedAsync(1, Mon), Times.Once);
    }

    [Fact] // Inline add-task on a group inserts a Task under that request, then reloads + broadcasts.
    public async Task AddTask_OnGroup_InsertsTaskUnderRequest_AndReloads()
    {
        var groups = new[] { new WeekBacklogGroup(42, "REQ-001", "Proj", System.Array.Empty<WeekRow>()) };
        var tl = new Mock<ITimeLogService>();
        var si = new Mock<ISmartInputService>();
        var tasks = new Mock<ITaskRepository>();
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed);
        tl.Setup(t => t.GetWeekGroupedAsync(1, It.IsAny<DateOnly>())).ReturnsAsync(groups);

        var vm = new TimesheetViewModel(
            tl.Object, tasks.Object, si.Object, clock.Object, () => 1, new WeakReferenceMessenger());
        await vm.LoadCommand.ExecuteAsync(null);
        tl.Invocations.Clear();

        var grp = vm.Groups[0];
        grp.NewTaskName = "  New Task  ";
        await grp.AddTaskCommand.ExecuteAsync(null);

        tasks.Verify(r => r.InsertAsync(It.Is<TaskItem>(
            t => t.BacklogId == 42 && t.TaskName == "New Task" && t.IsActive)), Times.Once);
        // Add reloads the grid (then broadcasts; the VM also self-reloads on that broadcast -> >=1).
        tl.Verify(t => t.GetWeekGroupedAsync(1, It.IsAny<DateOnly>()), Times.AtLeastOnce);
        Assert.Equal("", grp.NewTaskName); // cleared after add
    }

    [Fact] // Blank task name is a no-op: no insert, no reload.
    public async Task AddTask_BlankName_DoesNothing()
    {
        var groups = new[] { new WeekBacklogGroup(42, "REQ-001", "Proj", System.Array.Empty<WeekRow>()) };
        var (vm, tl, _) = Make(groups);
        await vm.LoadCommand.ExecuteAsync(null);

        var grp = vm.Groups[0];
        grp.NewTaskName = "   ";
        await grp.AddTaskCommand.ExecuteAsync(null);

        // initial GetWeekGroupedAsync ran once (load); no extra reload from a no-op add.
        tl.Verify(t => t.GetWeekGroupedAsync(1, It.IsAny<DateOnly>()), Times.Once);
    }
}
