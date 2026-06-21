using Moq;
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

    private static WeekGrid Grid(DateOnly monday, params WeekRow[] rows) => new(monday, rows);

    private static (TimesheetViewModel vm, Mock<ITimeLogService> tl, Mock<ISmartInputService> si) Make(
        WeekGrid? initial = null, int userId = 1)
    {
        var tl = new Mock<ITimeLogService>();
        var si = new Mock<ISmartInputService>();
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.Today).Returns(Wed);

        tl.Setup(t => t.GetWeekAsync(userId, It.IsAny<DateOnly>()))
          .ReturnsAsync((int _, DateOnly m) => initial ?? Grid(m));
        tl.Setup(t => t.SaveCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>(), It.IsAny<decimal>()))
          .ReturnsAsync(new SaveResult(true, null));
        tl.Setup(t => t.ClearCellAsync(userId, It.IsAny<int>(), It.IsAny<DateOnly>()))
          .Returns(Task.CompletedTask);

        var vm = new TimesheetViewModel(tl.Object, si.Object, clock.Object, () => userId);
        return (vm, tl, si);
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
        Assert.Equal("Mon 15/06", vm.MonHeader);
        Assert.Equal("Fri 19/06", vm.FriHeader);
    }

    [Fact]
    public async Task Next_ShiftsWeekForwardSevenDays_AndReloads()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.NextWeekCommand.ExecuteAsync(null);
        Assert.Equal(Mon.AddDays(7), vm.CurrentWeek);
        tl.Verify(t => t.GetWeekAsync(1, Mon.AddDays(7)), Times.Once);
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
    public async Task Rows_ShapedFromWeekGrid_OneRowPerTask()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, 3m, null, null),
            new WeekRow(9, "DEFAULT", "Annual Leave", 0, null, null, null, null, 8m));
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(7, vm.Rows[0].TaskId);
        Assert.Equal(4m, vm.Rows[0].Mon);
        Assert.Equal("Annual Leave", vm.Rows[1].TaskName);
    }

    [Fact]
    public async Task ColumnTotals_SumAllRows_AndUpdateOnCellChange()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 2m, null, null, null, null));
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(6m, vm.MonTotal);

        vm.Rows[0].Mon = 5m;               // 5 + 2
        Assert.Equal(7m, vm.MonTotal);
    }

    [Fact]
    public async Task Save_DisabledWhenAnyColumnExceedsEight()
    {
        var grid = Grid(Mon,
            new WeekRow(7, "REQ-001", "Implement", 0, 5m, null, null, null, null),
            new WeekRow(9, "DEFAULT", "Meeting", 1, 4m, null, null, null, null)); // Mon = 9 > 8
        var (vm, _, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Rows[1].Mon = 2m;               // Mon now 7
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCell_WithValue_UpsertsOnNaturalKey()
    {
        var grid = Grid(Mon, new WeekRow(7, "REQ-001", "Implement", 0, null, null, null, null, null));
        var (vm, tl, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);
        // value still null -> nothing yet
        vm.Rows[0].Mon = 4m;
        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);

        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, 4m), Times.Once);
    }

    [Fact]
    public async Task SaveCell_WithEmptyValue_DeletesLog()
    {
        var grid = Grid(Mon, new WeekRow(7, "REQ-001", "Implement", 0, 4m, null, null, null, null));
        var (vm, tl, _) = Make(grid);
        await vm.LoadCommand.ExecuteAsync(null);

        vm.Rows[0].Mon = null;             // cleared
        await vm.SaveCellAsync(vm.Rows[0], DayColumn.Mon);

        tl.Verify(t => t.ClearCellAsync(1, 7, Mon), Times.Once);
        tl.Verify(t => t.SaveCellAsync(1, 7, Mon, It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task SmartInputApplied_ReloadsWeek()
    {
        var (vm, tl, _) = Make();
        await vm.LoadCommand.ExecuteAsync(null);
        tl.Invocations.Clear();

        vm.RaiseSmartInputAppliedForTest();   // internal test hook -> ReloadAsync

        tl.Verify(t => t.GetWeekAsync(1, Mon), Times.Once);
    }
}
