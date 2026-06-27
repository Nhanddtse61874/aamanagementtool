using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>Pure schedule-chip decision (spec §4.3/§6.4). Late takes precedence over Warning;
/// Done suppresses both. Never throws — all division is guarded (R4).</summary>
public sealed class ScheduleStateService : IScheduleStateService
{
    public ScheduleState Evaluate(
        DateOnly today, DateOnly? startDate, DateOnly? internalDeadline,
        decimal? estimateHours, decimal loggedHours, bool isDone,
        IReadOnlySet<DateOnly> holidays, IWorkingDayCalculator calc)
    {
        // 1. Done backlog → no chip.
        if (isDone) return ScheduleState.Normal;

        // 2. Nothing to be late/behind against.
        if (internalDeadline is not { } deadline) return ScheduleState.Normal;

        // 3. Past the internal deadline (and not Done) → Late, takes precedence over Warning.
        if (today > deadline) return ScheduleState.Late;

        // 4. Warning: needs start_date + a positive estimate, within ≤2 working days, and behind.
        if (startDate is not { } start) return ScheduleState.Normal;
        if (estimateHours is not { } estimate || estimate <= 0m) return ScheduleState.Normal;

        // ≤2-day window: working days in (today, deadline] ≤ 2.
        var nearStart = today.AddDays(1);
        var daysToDeadline = nearStart <= deadline
            ? calc.CountWorkingDays(nearStart, deadline, holidays)
            : 0;
        if (daysToDeadline > 2) return ScheduleState.Normal;

        // behind = logged/estimate < elapsed/total. total<=0 → undefined → not behind.
        var total = calc.CountWorkingDays(start, deadline, holidays);
        if (total <= 0) return ScheduleState.Normal;

        var upper = today < deadline ? today : deadline;       // clamp elapsed to the deadline
        var elapsed = calc.CountWorkingDays(start, upper, holidays);

        // Cross-multiply to avoid divide-by-zero entirely: logged/estimate < elapsed/total
        //   ⇔ loggedHours * total < elapsed * estimate   (estimate>0, total>0 ⇒ no sign flip).
        var behind = loggedHours * total < (decimal)elapsed * estimate;
        return behind ? ScheduleState.Warning : ScheduleState.Normal;
    }
}
