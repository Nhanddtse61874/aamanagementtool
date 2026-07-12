using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Pure smart-input distribution math (no DB, no IClock). Contract is VERBATIM from spec §4,
// extended in P8 with holiday-aware overloads (HOL-02) that exclude marked Holidays as well as weekends.
public interface ISmartInputService
{
    // Pure computation — no storage dependency. Returns preview cells (SI-06).
    SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours);  // SI-01/02/03
    SmartInputResult FillFull8h(DateOnly from, DateOnly to);                          // SI-02/04

    // P8 (HOL-02): same math, but also skips dates in the holiday set. The parameterless overloads
    // above delegate to these with an empty set (existing SI-01/02 behavior unchanged).
    SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours, IReadOnlySet<DateOnly> holidays);
    SmartInputResult FillFull8h(DateOnly from, DateOnly to, IReadOnlySet<DateOnly> holidays);
}
