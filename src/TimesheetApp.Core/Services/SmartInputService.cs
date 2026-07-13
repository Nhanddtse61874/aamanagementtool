using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Pure integer-tenths distribution math. No DB, no IClock. (SI-01/02/03/04)
/// P8: holiday-aware overloads route working-day enumeration through <see cref="IWorkingDayCalculator"/>;
/// the legacy overloads delegate with an empty holiday set so SI-01/02 behavior is unchanged (HOL-02, A3).</summary>
public sealed class SmartInputService : ISmartInputService
{
    private const int DayCapTenths = 80; // 8.0h — the per-day ceiling a Full-8h fill tops up to

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

    // --- Multi-task fill (M8.2: moved verbatim from SmartInputPanelVm.BuildPlan) -------------------
    //
    // Two tiers, both in integer tenths so no intermediate step ever touches a float (PITFALL §2):
    //   tier 1 (Split evenly): the grand total is split across the N checked tasks, remainder to the
    //                          FIRST tasks;
    //   tier 2:                each task's tenths are split across the D working days, remainder to the
    //                          LAST day.
    // Full 8h skips tier 1: every working day is topped up to 8.0h, split across the checked tasks with
    // the remainder on the FIRST tasks (3 tasks -> 27/27/26 tenths -> 2.7 + 2.7 + 2.6 = 8.0h/day).
    //
    // The asymmetry (first-task vs last-day) is deliberate and preserved exactly: for a holiday-free
    // range this returns what BuildPlan returned, tenth for tenth. The ONLY behavioral change is that
    // `days` now comes from the holiday-aware calculator, so a holiday no longer gets a preview cell
    // that ValidateSmartFillAsync would then reject.
    public SmartFillPlanResult BuildPlan(
        SmartInputMode mode, DateOnly from, DateOnly to, decimal totalHours,
        IReadOnlyList<int> taskIds, IReadOnlySet<DateOnly> holidays)
    {
        // Guard before the divisions below — n == 0 would divide by zero in the Split-evenly tier.
        if (taskIds.Count == 0) return PlanFail("Select at least one task.");

        var days = _calc.WorkingDaysBetween(from, to, holidays);
        if (days.Count == 0) return PlanFail("No working days in the selected range.");

        var n = taskIds.Count;

        // Tier 1 — per-task total tenths (Split evenly only); remainder to the FIRST tasks.
        int[]? perTaskTenths = null;
        if (mode == SmartInputMode.DistributeEven)
        {
            if (totalHours <= 0m) return PlanFail("Total hours must be greater than 0.");
            if (HasMoreThanOneDecimal(totalHours))
                return PlanFail("Total hours may have at most 1 decimal place.");

            var totalTenths = (int)Math.Round(totalHours * 10m, MidpointRounding.AwayFromZero);
            var b = totalTenths / n;
            var r = totalTenths % n;
            perTaskTenths = Enumerable.Range(0, n).Select(i => b + (i < r ? 1 : 0)).ToArray();
        }

        var plan = new List<SmartFillTask>();
        for (var i = 0; i < n; i++)
        {
            var cells = new List<CellAssignment>();
            if (mode == SmartInputMode.DistributeEven)
            {
                // Tier 2 — split this task's tenths across the working days; remainder on the LAST day.
                var tt = perTaskTenths![i];
                var db = tt / days.Count;
                var dr = tt % days.Count;
                for (var j = 0; j < days.Count; j++)
                {
                    var tenths = db + (j == days.Count - 1 ? dr : 0);
                    if (tenths > 0) cells.Add(new CellAssignment(days[j], tenths / 10m));
                }
            }
            else // Full 8h: top each working day up to 8h, split equally across the checked tasks.
            {
                var perDay = DayCapTenths / n + (i < DayCapTenths % n ? 1 : 0);
                if (perDay > 0)
                    foreach (var d in days) cells.Add(new CellAssignment(d, perDay / 10m));
            }
            if (cells.Count > 0) plan.Add(new SmartFillTask(taskIds[i], cells));
        }

        return plan.Count == 0 ? PlanFail("Nothing to distribute.") : new SmartFillPlanResult(true, plan, null);
    }

    private static bool HasMoreThanOneDecimal(decimal v)
        => v != Math.Round(v, 1, MidpointRounding.AwayFromZero);

    private static SmartInputResult Fail(string message)
        => new(false, Array.Empty<CellAssignment>(), message);

    private static SmartFillPlanResult PlanFail(string message)
        => new(false, Array.Empty<SmartFillTask>(), message);
}
