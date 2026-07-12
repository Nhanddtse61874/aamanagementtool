using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Pure integer-tenths distribution math. No DB, no IClock. (SI-01/02/03/04)
/// P8: holiday-aware overloads route working-day enumeration through <see cref="IWorkingDayCalculator"/>;
/// the legacy overloads delegate with an empty holiday set so SI-01/02 behavior is unchanged (HOL-02, A3).</summary>
public sealed class SmartInputService : ISmartInputService
{
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    private readonly IWorkingDayCalculator _calc;

    // Optional ctor param keeps `new SmartInputService()` (tests) + the parameterless DI registration working.
    public SmartInputService(IWorkingDayCalculator? calc = null) => _calc = calc ?? new WorkingDayCalculator();

    public SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours)
        => DistributeEven(from, to, totalHours, NoHolidays);

    public SmartInputResult FillFull8h(DateOnly from, DateOnly to)
        => FillFull8h(from, to, NoHolidays);

    public SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours, IReadOnlySet<DateOnly> holidays)
    {
        if (totalHours <= 0m)
            return Fail("Total hours must be greater than 0.");
        if (HasMoreThanOneDecimal(totalHours))
            return Fail("Total hours may have at most 1 decimal place.");

        var days = _calc.WorkingDaysBetween(from, to, holidays);
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

    public SmartInputResult FillFull8h(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays)
    {
        var days = _calc.WorkingDaysBetween(from, to, holidays);
        if (days.Count == 0)
            return Fail("No working days in the selected range.");

        var cells = days.Select(d => new CellAssignment(d, 8m)).ToList();
        return new SmartInputResult(true, cells, null);
    }

    private static bool HasMoreThanOneDecimal(decimal v)
        => v != Math.Round(v, 1, MidpointRounding.AwayFromZero);

    private static SmartInputResult Fail(string message)
        => new(false, Array.Empty<CellAssignment>(), message);
}
