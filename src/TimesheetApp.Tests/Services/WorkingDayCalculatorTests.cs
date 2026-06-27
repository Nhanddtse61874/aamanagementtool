using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// HOL-02: pure working-day math — weekends + holidays excluded, inclusive counts, edges.
public class WorkingDayCalculatorTests
{
    private readonly IWorkingDayCalculator _calc = new WorkingDayCalculator();
    private static IReadOnlySet<DateOnly> None => new HashSet<DateOnly>();
    private static IReadOnlySet<DateOnly> Holidays(params DateOnly[] d) => d.ToHashSet();

    [Theory]
    [InlineData(2026, 6, 15, true)]   // Mon
    [InlineData(2026, 6, 19, true)]   // Fri
    [InlineData(2026, 6, 20, false)]  // Sat
    [InlineData(2026, 6, 21, false)]  // Sun
    public void IsWorkingDay_excludes_weekends(int y, int m, int d, bool expected)
        => Assert.Equal(expected, _calc.IsWorkingDay(new DateOnly(y, m, d), None));

    [Fact]
    public void IsWorkingDay_excludes_a_marked_holiday()
    {
        var holiday = new DateOnly(2026, 6, 17); // a Wednesday
        Assert.True(_calc.IsWorkingDay(holiday, None));
        Assert.False(_calc.IsWorkingDay(holiday, Holidays(holiday)));
    }

    [Fact]
    public void WorkingDaysBetween_skips_weekend_and_holiday()
    {
        // Mon 06-15 .. Mon 06-22, holiday on Wed 06-17.
        var from = new DateOnly(2026, 6, 15);
        var to = new DateOnly(2026, 6, 22);
        var days = _calc.WorkingDaysBetween(from, to, Holidays(new DateOnly(2026, 6, 17)));

        Assert.Equal(new[]
        {
            new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 16),
            new DateOnly(2026, 6, 18), new DateOnly(2026, 6, 19),
            new DateOnly(2026, 6, 22),
        }, days);
    }

    [Fact]
    public void CountWorkingDays_is_inclusive_both_ends()
    {
        // Mon 06-15 .. Fri 06-19 inclusive = 5 working days.
        Assert.Equal(5, _calc.CountWorkingDays(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19), None));
    }

    [Fact]
    public void CountWorkingDays_single_working_day_is_one()
        => Assert.Equal(1, _calc.CountWorkingDays(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 15), None));

    [Fact]
    public void CountWorkingDays_single_weekend_day_is_zero()
        => Assert.Equal(0, _calc.CountWorkingDays(new DateOnly(2026, 6, 20), new DateOnly(2026, 6, 20), None));

    [Fact]
    public void CountWorkingDays_subtracts_a_holiday_inside_the_range()
    {
        var holiday = new DateOnly(2026, 6, 17);
        Assert.Equal(5, _calc.CountWorkingDays(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19), None));
        Assert.Equal(4, _calc.CountWorkingDays(new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 19), Holidays(holiday)));
    }

    [Fact]
    public void CountWorkingDays_from_after_to_is_zero()
        => Assert.Equal(0, _calc.CountWorkingDays(new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 15), None));

    [Fact]
    public void WorkingDaysBetween_from_after_to_is_empty()
        => Assert.Empty(_calc.WorkingDaysBetween(new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 15), None));
}
