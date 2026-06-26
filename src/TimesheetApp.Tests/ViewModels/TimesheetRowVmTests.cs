using System.ComponentModel;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

public class TimesheetRowVmTests
{
    private static TimesheetRowVm NewRow() => new()
    {
        TaskId = 7,
        BacklogCode = "REQ-001",
        Project = "ProjectX",
        TaskName = "Implement"
    };

    [Fact]
    public void RowTotal_SumsNonNullDays_TreatingNullAsZero()
    {
        var row = NewRow();
        row.Mon = 4m; row.Wed = 3.5m; // Tue/Thu/Fri null
        Assert.Equal(7.5m, row.RowTotal);
    }

    [Fact]
    public void SettingDay_RoundsToOneDecimalAwayFromZero()
    {
        var row = NewRow();
        row.Mon = 2.45m;
        Assert.Equal(2.5m, row.Mon);
    }

    [Fact]
    public void SettingDay_RaisesDayChanged()
    {
        var row = NewRow();
        var fired = 0;
        DayColumn? changed = null;
        row.DayChanged += (_, col) => { fired++; changed = col; };
        row.Tue = 5m;
        Assert.Equal(1, fired);
        Assert.Equal(DayColumn.Tue, changed);
    }

    [Fact]
    public void EmptyCell_IsNull_NotZero()
    {
        var row = NewRow();
        row.Fri = null;
        Assert.Null(row.Fri);
    }

    [Theory]
    [InlineData(9)]      // > 8h single cell
    [InlineData(0)]      // <= 0
    [InlineData(-1)]     // negative
    public void InvalidValue_AddsErrorForThatColumn(decimal bad)
    {
        var row = NewRow();
        row.Mon = bad;
        Assert.True(row.HasErrors);
        Assert.NotEmpty(System.Linq.Enumerable.Cast<object>(row.GetErrors(nameof(row.Mon))));
    }

    [Fact]
    public void MoreThanOneDecimal_AddsError()
    {
        var row = NewRow();
        row.Mon = 2.55m; // 2 decimals — rejected (not silently rounded for validation purposes)
        Assert.True(row.HasErrors);
    }

    [Fact]
    public void ValidValue_ClearsPriorError_AndRaisesErrorsChanged()
    {
        var row = NewRow();
        DataErrorsChangedEventArgs? evt = null;
        row.ErrorsChanged += (_, e) => evt = e;
        row.Mon = 9m;          // error
        Assert.True(row.HasErrors);
        row.Mon = 4m;          // valid
        Assert.False(row.HasErrors);
        Assert.NotNull(evt);
    }
}
