using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>How a smart fill spreads hours: split a grand total, or top every working day up to 8h.</summary>
// M8.2: moved out of SmartInputPanelVm (a WPF ViewModel) so the fill contract lives in Core, where the
// web client can reuse it instead of re-deriving it.
public enum SmartInputMode { DistributeEven, FillFull8h }

/// <summary>A whole multi-task fill: one <see cref="SmartFillTask"/> per checked task, or an error.</summary>
public sealed record SmartFillPlanResult(bool Ok, IReadOnlyList<SmartFillTask> Tasks, string? Error);

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

    // M8.2: the multi-task fill (tasks × working days) that the Smart Fill panel actually runs. It used
    // to live in SmartInputPanelVm.BuildPlan, which enumerated working days WITHOUT excluding holidays
    // while ValidateSmartFillAsync DID — so a range containing a holiday previewed a cell that then
    // failed validation and blocked Apply. Here it shares the one holiday-aware day enumeration.
    SmartFillPlanResult BuildPlan(
        SmartInputMode mode, DateOnly from, DateOnly to, decimal totalHours,
        IReadOnlyList<int> taskIds, IReadOnlySet<DateOnly> holidays);
}
