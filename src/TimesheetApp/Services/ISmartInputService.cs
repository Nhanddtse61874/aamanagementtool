using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Pure smart-input distribution math (no DB, no IClock). Contract is VERBATIM from spec §4.
public interface ISmartInputService
{
    // Pure computation — no storage dependency. Returns preview cells (SI-06).
    SmartInputResult DistributeEven(DateOnly from, DateOnly to, decimal totalHours);  // SI-01/02/03
    SmartInputResult FillFull8h(DateOnly from, DateOnly to);                          // SI-02/04
}
