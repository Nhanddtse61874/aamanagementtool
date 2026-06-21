using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Pure integer-tenths distribution math. No DB, no IClock. (SI-01/02/03/04)</summary>
public sealed class SmartInputService : ISmartInputService
{
    public SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours)
    {
        if (totalHours <= 0m)
            return Fail("Total hours must be greater than 0.");
        if (HasMoreThanOneDecimal(totalHours))
            return Fail("Total hours may have at most 1 decimal place.");

        var days = WorkingDays(from, to);
        if (days.Count == 0)
            return Fail("No working days in the selected range.");

        // Integer tenths avoids binary-float drift (PITFALL §2).
        var totalTenths = (int)Math.Round(totalHours * 10m, MidpointRounding.AwayFromZero);
        var baseTenths = totalTenths / days.Count;        // floor
        var remainder = totalTenths % days.Count;         // tenths dumped on last working day

        var cells = new List<CellAssignment>(days.Count);
        for (var i = 0; i < days.Count; i++)
        {
            var tenths = baseTenths + (i == days.Count - 1 ? remainder : 0);
            cells.Add(new CellAssignment(days[i], tenths / 10m));
        }

        return new SmartInputResult(true, cells, null);
    }

    public SmartInputResult FillFull8h(DateOnly from, DateOnly to)
    {
        var days = WorkingDays(from, to);
        if (days.Count == 0)
            return Fail("No working days in the selected range.");

        var cells = days.Select(d => new CellAssignment(d, 8m)).ToList();
        return new SmartInputResult(true, cells, null);
    }

    // Mon–Fri only; culture-independent enum values (XC-05, SI-02).
    private static List<DateOnly> WorkingDays(DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                days.Add(d);
        return days;
    }

    private static bool HasMoreThanOneDecimal(decimal v)
        => v != Math.Round(v, 1, MidpointRounding.AwayFromZero);

    private static SmartInputResult Fail(string message)
        => new(false, Array.Empty<CellAssignment>(), message);
}
