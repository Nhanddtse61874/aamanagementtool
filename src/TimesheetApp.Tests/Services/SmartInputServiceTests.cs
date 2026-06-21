using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class SmartInputServiceTests
{
    private readonly ISmartInputService _svc = new SmartInputService();

    // ---- SI-01 core example (spec §7.1) ----
    [Fact]
    public void DistributeEven_10h_over_3_weekdays_gives_3_3_3_3_3_4()
    {
        // Mon 2026-06-15 .. Wed 2026-06-17 = 3 weekdays
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), 10m);

        Assert.True(r.Ok);
        Assert.Equal(new[] { 3.3m, 3.3m, 3.4m }, r.Cells.Select(c => c.Hours).ToArray());
        Assert.Equal(new[]
        {
            new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 16), new DateOnly(2026, 6, 17)
        }, r.Cells.Select(c => c.Date).ToArray());
    }

    // ---- SI-01 property: parts always sum to total exactly (no float drift) ----
    [Theory]
    [InlineData(10.0, 3)]   // 3.3,3.3,3.4
    [InlineData(9.0, 3)]    // exact divide 3,3,3
    [InlineData(10.0, 7)]   // large remainder: six*1.4 + 1.6
    [InlineData(5.5, 1)]    // single day
    [InlineData(7.4, 5)]    // arbitrary
    [InlineData(0.1, 4)]    // sub-base
    public void DistributeEven_parts_sum_exactly_to_total(double totalD, int weekdayCount)
    {
        var total = (decimal)totalD;
        // Build a range of exactly `weekdayCount` consecutive weekdays starting Mon 2026-06-15.
        var from = new DateOnly(2026, 6, 15);
        var to = from;
        var counted = 1;
        while (counted < weekdayCount)
        {
            to = to.AddDays(1);
            if (to.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) counted++;
        }

        var r = _svc.DistributeEven(from, to, total);

        Assert.True(r.Ok);
        Assert.Equal(weekdayCount, r.Cells.Count);
        Assert.Equal(total, r.Cells.Sum(c => c.Hours)); // exact decimal equality
    }

    [Fact]
    public void DistributeEven_remainder_lands_on_last_working_day()
    {
        // 10h / 7 weekdays => base 1.4, remainder 2 tenths on last day => 1.6
        var from = new DateOnly(2026, 6, 15);              // Mon
        var to = new DateOnly(2026, 6, 23);               // Tue next week (7 weekdays, skips Sat/Sun)
        var r = _svc.DistributeEven(from, to, 10m);

        Assert.True(r.Ok);
        Assert.Equal(7, r.Cells.Count);
        Assert.All(r.Cells.Take(6), c => Assert.Equal(1.4m, c.Hours));
        Assert.Equal(1.6m, r.Cells[^1].Hours);
        Assert.Equal(10m, r.Cells.Sum(c => c.Hours));
    }

    // ---- SI-02: weekends excluded ----
    [Fact]
    public void DistributeEven_skips_weekend_days_in_range()
    {
        // Fri 2026-06-19 .. Mon 2026-06-22 spans Sat+Sun => only Fri + Mon count
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 22), 4m);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Cells.Count);
        Assert.DoesNotContain(r.Cells, c => c.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    // ---- SI-03: guards ----
    [Fact]
    public void DistributeEven_all_weekend_range_is_no_op()
    {
        // Sat 2026-06-20 .. Sun 2026-06-21
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 20), new DateOnly(2026, 6, 21), 8m);
        Assert.False(r.Ok);
        Assert.Empty(r.Cells);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void DistributeEven_from_after_to_is_no_op()
    {
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 17), new DateOnly(2026, 6, 15), 8m);
        Assert.False(r.Ok);
        Assert.Empty(r.Cells);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(2.55)]   // >1 decimal place — must be rejected, not silently rounded
    public void DistributeEven_bad_total_is_no_op(double badTotalD)
    {
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), (decimal)badTotalD);
        Assert.False(r.Ok);
        Assert.Empty(r.Cells);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    // ---- SI-04: Full 8h ----
    [Fact]
    public void FillFull8h_assigns_8h_to_each_weekday_only()
    {
        // Fri 2026-06-19 .. Mon 2026-06-22 => Fri + Mon = 2 weekdays
        var r = _svc.FillFull8h(new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 22));

        Assert.True(r.Ok);
        Assert.Equal(2, r.Cells.Count);
        Assert.All(r.Cells, c => Assert.Equal(8m, c.Hours));
        Assert.DoesNotContain(r.Cells, c => c.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void FillFull8h_all_weekend_range_is_no_op()
    {
        var r = _svc.FillFull8h(new DateOnly(2026, 6, 20), new DateOnly(2026, 6, 21));
        Assert.False(r.Ok);
        Assert.Empty(r.Cells);
    }
}
