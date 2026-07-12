using TimesheetApp.Models;

namespace TimesheetApp.Services;

// Pure schedule-chip evaluation (TL-07/08). NEVER throws (R4): every divide is guarded.
// The caller resolves estimateHours = official ?? rough and loads the holiday set + the shared calculator.
public interface IScheduleStateService
{
    ScheduleState Evaluate(
        DateOnly today, DateOnly? startDate, DateOnly? internalDeadline,
        decimal? estimateHours, decimal loggedHours, bool isDone,
        IReadOnlySet<DateOnly> holidays, IWorkingDayCalculator calc);
}
