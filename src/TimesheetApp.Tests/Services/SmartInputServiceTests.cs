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

    // ---- HOL-02: holiday-aware overloads (a holiday in the range gets no hours) ----
    [Fact]
    public void DistributeEven_holiday_overload_excludes_a_marked_holiday()
    {
        // Mon 06-15 .. Wed 06-17 = 3 weekdays; mark Tue 06-16 as a holiday -> only Mon + Wed.
        var holidays = new HashSet<DateOnly> { new(2026, 6, 16) };
        var r = _svc.DistributeEven(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), 10m, holidays);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Cells.Count);
        Assert.DoesNotContain(r.Cells, c => c.Date == new DateOnly(2026, 6, 16));
        Assert.Equal(10m, r.Cells.Sum(c => c.Hours));   // total still preserved across the 2 working days
    }

    [Fact]
    public void DistributeEven_no_holiday_overload_is_unchanged_by_empty_set()
    {
        var baseline = _svc.DistributeEven(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), 10m);
        var withEmpty = _svc.DistributeEven(
            new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), 10m, new HashSet<DateOnly>());

        Assert.Equal(baseline.Cells.Select(c => c.Hours), withEmpty.Cells.Select(c => c.Hours));
        Assert.Equal(baseline.Cells.Select(c => c.Date), withEmpty.Cells.Select(c => c.Date));
    }

    [Fact]
    public void FillFull8h_holiday_overload_excludes_a_marked_holiday()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 6, 16) };
        var r = _svc.FillFull8h(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 17), holidays);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Cells.Count);
        Assert.DoesNotContain(r.Cells, c => c.Date == new DateOnly(2026, 6, 16));
        Assert.All(r.Cells, c => Assert.Equal(8m, c.Hours));
    }

    // ---- M8.2: BuildPlan, the multi-task fill moved here from SmartInputPanelVm ----

    private static readonly DateOnly Mon = new(2026, 6, 15);
    private static readonly DateOnly Tue = new(2026, 6, 16);
    private static readonly DateOnly Wed = new(2026, 6, 17);
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    // The parity lock. This case has a remainder in BOTH tiers, so it pins the asymmetry that the old
    // BuildPlan had and that the move to Core had to preserve exactly: 10.0h = 100 tenths across 3 tasks
    // -> 34/33/33 (remainder to the FIRST task), then each task's tenths across Mon/Tue/Wed with the
    // remainder on the LAST day (34 -> 11/11/12). Change either rule and this test fails.
    [Fact]
    public void BuildPlan_splits_tasks_then_days_in_integer_tenths_remainder_first_task_then_last_day()
    {
        var r = _svc.BuildPlan(SmartInputMode.DistributeEven, Mon, Wed, 10.0m, new[] { 7, 8, 9 }, NoHolidays);

        Assert.True(r.Ok);
        Assert.Equal(3, r.Tasks.Count);

        // task 7 got the tier-1 remainder (3.4h), and its tier-2 remainder landed on Wed (the LAST day).
        Assert.Equal(new[] { 1.1m, 1.1m, 1.2m }, r.Tasks[0].Cells.Select(c => c.Hours));
        Assert.Equal(new[] { Mon, Tue, Wed }, r.Tasks[0].Cells.Select(c => c.Date));
        Assert.Equal(new[] { 1.1m, 1.1m, 1.1m }, r.Tasks[1].Cells.Select(c => c.Hours));
        Assert.Equal(new[] { 1.1m, 1.1m, 1.1m }, r.Tasks[2].Cells.Select(c => c.Hours));

        // Nothing is lost or invented by the two-tier split.
        Assert.Equal(10.0m, r.Tasks.SelectMany(t => t.Cells).Sum(c => c.Hours));
    }

    // The bug: BuildPlan enumerated working days WITHOUT excluding holidays, so a holiday got a preview
    // cell that ValidateSmartFillAsync then rejected. The fill now skips it — and still places all 9h.
    [Fact]
    public void BuildPlan_excludes_holidays_and_redistributes_the_hours_across_the_remaining_days()
    {
        var holidays = new HashSet<DateOnly> { Tue };

        var r = _svc.BuildPlan(SmartInputMode.DistributeEven, Mon, Wed, 9m, new[] { 7 }, holidays);

        Assert.True(r.Ok);
        var cells = Assert.Single(r.Tasks).Cells;
        Assert.DoesNotContain(cells, c => c.Date == Tue);
        Assert.Equal(new[] { Mon, Wed }, cells.Select(c => c.Date));
        Assert.Equal(9m, cells.Sum(c => c.Hours));   // all 9h still placed, just not on the holiday
    }

    // Full 8h tops each working day up to the 8.0h cap, split across the checked tasks with the
    // remainder on the FIRST tasks: 80 tenths / 3 -> 27, 27, 26 -> 2.7 + 2.7 + 2.6 = 8.0h per day.
    [Fact]
    public void BuildPlan_Full8h_splits_the_day_cap_across_tasks_with_the_remainder_on_the_first()
    {
        var r = _svc.BuildPlan(SmartInputMode.FillFull8h, Mon, Wed, totalHours: 0m, new[] { 7, 8, 9 }, NoHolidays);

        Assert.True(r.Ok);
        Assert.Equal(new[] { 2.7m, 2.7m, 2.6m }, r.Tasks.Select(t => t.Cells[0].Hours));
        Assert.All(r.Tasks, t => Assert.Equal(3, t.Cells.Count));   // every working day filled

        foreach (var day in new[] { Mon, Tue, Wed })
            Assert.Equal(8.0m, r.Tasks.SelectMany(t => t.Cells).Where(c => c.Date == day).Sum(c => c.Hours));
    }

    [Fact]
    public void BuildPlan_with_no_working_days_in_range_fails_instead_of_dividing_by_zero()
    {
        var sat = new DateOnly(2026, 6, 20);
        var sun = new DateOnly(2026, 6, 21);

        var r = _svc.BuildPlan(SmartInputMode.DistributeEven, sat, sun, 8m, new[] { 7 }, NoHolidays);

        Assert.False(r.Ok);
        Assert.Empty(r.Tasks);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void BuildPlan_with_no_tasks_fails_instead_of_dividing_by_zero()
    {
        var r = _svc.BuildPlan(SmartInputMode.DistributeEven, Mon, Wed, 8m, Array.Empty<int>(), NoHolidays);

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }
}
