using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

// TL-07/08 decision table (§6.4). Late > Warning > Normal; Done suppresses; never throws (R4).
public class ScheduleStateServiceTests
{
    private readonly IScheduleStateService _svc = new ScheduleStateService();
    private readonly IWorkingDayCalculator _calc = new WorkingDayCalculator();
    private static IReadOnlySet<DateOnly> None => new HashSet<DateOnly>();

    // Anchor dates (all weekdays):
    private static readonly DateOnly Today = new(2026, 6, 17);   // Wed
    private static readonly DateOnly Start = new(2026, 6, 15);   // Mon
    private static readonly DateOnly DeadlineNear = new(2026, 6, 19);  // Fri — (today,deadline] = 2 wd → in window
    private static readonly DateOnly DeadlineFar = new(2026, 6, 23);   // Tue next week — 3 wd → out of window

    private ScheduleState Eval(
        DateOnly? start, DateOnly? deadline, decimal? estimate, decimal logged, bool done,
        DateOnly? today = null)
        => _svc.Evaluate(today ?? Today, start, deadline, estimate, logged, done, None, _calc);

    [Fact]
    public void Done_suppresses_everything()
    {
        // Past deadline + behind, but Done → Normal.
        Assert.Equal(ScheduleState.Normal,
            Eval(Start, new DateOnly(2026, 6, 10), 10m, 0m, done: true));
    }

    [Fact]
    public void No_internal_deadline_is_normal()
        => Assert.Equal(ScheduleState.Normal, Eval(Start, null, 10m, 0m, done: false));

    [Fact]
    public void Past_internal_deadline_not_done_is_late()
        => Assert.Equal(ScheduleState.Late, Eval(Start, new DateOnly(2026, 6, 16), 10m, 0m, done: false));

    [Fact]
    public void Late_takes_precedence_even_with_no_estimate_or_start()
        => Assert.Equal(ScheduleState.Late, Eval(null, new DateOnly(2026, 6, 16), null, 0m, done: false));

    [Fact]
    public void Within_window_and_behind_is_warning()
    {
        // total=5 (Mon..Fri), elapsed=3 (Mon..Wed), 0.5 < 0.6 → behind.
        Assert.Equal(ScheduleState.Warning, Eval(Start, DeadlineNear, 10m, 5m, done: false));
    }

    [Fact]
    public void Within_window_on_or_ahead_of_schedule_is_normal()
    {
        // 8/10 = 0.8 ≥ elapsed 3/5 = 0.6 → not behind.
        Assert.Equal(ScheduleState.Normal, Eval(Start, DeadlineNear, 10m, 8m, done: false));
    }

    [Fact]
    public void Behind_but_outside_two_day_window_is_normal()
    {
        // DeadlineFar = 3 working days away → not "near" → no warning even though behind.
        Assert.Equal(ScheduleState.Normal, Eval(Start, DeadlineFar, 10m, 0m, done: false));
    }

    [Fact]
    public void Null_estimate_within_window_behind_is_normal()
        => Assert.Equal(ScheduleState.Normal, Eval(Start, DeadlineNear, null, 0m, done: false));

    [Fact]
    public void Zero_estimate_within_window_is_normal_no_divide_by_zero()
        => Assert.Equal(ScheduleState.Normal, Eval(Start, DeadlineNear, 0m, 0m, done: false));

    [Fact]
    public void No_start_date_within_window_is_normal_warning_needs_start()
        => Assert.Equal(ScheduleState.Normal, Eval(null, DeadlineNear, 10m, 0m, done: false));

    [Fact]
    public void Reversed_deadline_total_le_zero_is_normal_no_throw()
    {
        // start AFTER deadline → total<=0 → not behind → Normal. today<=deadline so not Late.
        var start = new DateOnly(2026, 6, 19);
        var deadline = new DateOnly(2026, 6, 18);
        Assert.Equal(ScheduleState.Normal,
            _svc.Evaluate(new DateOnly(2026, 6, 18), start, deadline, 10m, 0m, false, None, _calc));
    }

    [Fact]
    public void Today_before_start_is_normal_elapsed_zero()
    {
        // today before start: elapsed=0 → logged>=0 ratio not < 0 → not behind.
        var today = new DateOnly(2026, 6, 12);                 // Fri before start
        var deadline = new DateOnly(2026, 6, 12);             // same day so within window check passes vacuously? no
        // Use a deadline within 2 wd of `today` to reach the behind branch, start in the future.
        Assert.Equal(ScheduleState.Normal,
            _svc.Evaluate(today, new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 16), 10m, 0m, false, None, _calc));
    }

    [Fact]
    public void Today_equals_deadline_within_window_behind_is_warning()
    {
        // today==deadline: not past (not Late); (today,deadline] = 0 wd ≤ 2 → in window.
        // total = Mon..Wed = 3, elapsed = Mon..Wed = 3 → 0/10 < 3/3 → behind.
        Assert.Equal(ScheduleState.Warning,
            _svc.Evaluate(Today, Start, Today, 10m, 0m, false, None, _calc));
    }

    [Fact]
    public void Two_day_boundary_exactly_two_working_days_is_in_window()
    {
        // (Wed, Fri] = {Thu, Fri} = 2 wd → in window; behind → Warning.
        Assert.Equal(ScheduleState.Warning, Eval(Start, DeadlineNear, 10m, 0m, done: false));
    }

    [Fact]
    public void Two_day_boundary_three_working_days_is_out_of_window()
    {
        // (Wed, Tue-next] = {Thu,Fri,Mon,Tue}? actually 06-23 Tue → Thu,Fri,Mon,Tue = 4... use 06-22 Mon.
        var deadline = new DateOnly(2026, 6, 22); // Mon: (Wed,Mon] = Thu,Fri,Mon = 3 wd → out.
        Assert.Equal(ScheduleState.Normal, Eval(Start, deadline, 10m, 0m, done: false));
    }
}
