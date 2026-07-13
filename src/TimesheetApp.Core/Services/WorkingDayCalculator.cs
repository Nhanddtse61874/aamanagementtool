namespace TimesheetApp.Services;

/// <summary>Pure working-day math (HOL-02): weekends + holidays excluded. No DB, no IClock.</summary>
public sealed class WorkingDayCalculator : IWorkingDayCalculator
{
    public bool IsWorkingDay(DateOnly d, IReadOnlySet<DateOnly> holidays)
        => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(d);

    public IReadOnlyList<DateOnly> WorkingDaysBetween(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsWorkingDay(d, holidays))
                days.Add(d);
        return days;
    }

    // Inclusive of both ends; from>to => 0 (empty range, never negative).
    public int CountWorkingDays(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
        => WorkingDaysBetween(from, to, holidays).Count;
}
