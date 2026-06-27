using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests.ViewModels;

// P8 / W6 (spec §5.4, Q3): GanttModel geometry — pure index math over a working-day axis
// (weekends + holidays excluded). Canvas pixel drawing is UAT, not tested here.
public sealed class GanttModelTests
{
    private static readonly IWorkingDayCalculator Calc = new WorkingDayCalculator();

    private static Backlog Bl(
        string code, DateOnly? start = null, DateOnly? internalDeadline = null,
        DateOnly? externalDeadline = null, DateOnly? end = null, int id = 1) =>
        new(id, code, "ARCS", DateTimeOffset.UtcNow,
            StartDate: start, EndDate: end,
            DeadlineInternal: internalDeadline, DeadlineExternal: externalDeadline);

    private static GanttModel Build(
        IReadOnlySet<DateOnly> holidays, params (Backlog, ScheduleState)[] src) =>
        TaskListViewModel.BuildGantt(src.ToList(), holidays, Calc);

    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    [Fact] // Axis is working days only: a Mon→Fri+next-Mon span has no Sat/Sun on the axis.
    public void Axis_excludes_weekends()
    {
        // Mon 2026-06-15 .. Mon 2026-06-22 → working days 15,16,17,18,19, 22 (skip Sat 20 / Sun 21).
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 22)), ScheduleState.Normal));

        Assert.Equal(6, model.Axis.Count);
        Assert.DoesNotContain(new DateOnly(2026, 6, 20), model.Axis);   // Sat
        Assert.DoesNotContain(new DateOnly(2026, 6, 21), model.Axis);   // Sun
        Assert.Equal(new DateOnly(2026, 6, 15), model.Axis[0]);
        Assert.Equal(new DateOnly(2026, 6, 22), model.Axis[^1]);
    }

    [Fact] // A holiday inside the span is excluded from the axis (one shared working-day calculator).
    public void Axis_excludes_holiday_inside_span()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 6, 17) };   // Wed marked as a holiday
        var model = Build(holidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 19)), ScheduleState.Normal));

        // Mon..Fri minus the Wed holiday = 15,16,18,19.
        Assert.Equal(4, model.Axis.Count);
        Assert.DoesNotContain(new DateOnly(2026, 6, 17), model.Axis);
    }

    [Fact] // A known start/deadline gives the expected StartDayIndex + working-day span.
    public void Bar_span_is_start_to_internal_deadline_in_working_days()
    {
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 19)), ScheduleState.Normal));

        var bar = Assert.Single(model.Bars);
        Assert.Equal(0, bar.StartDayIndex);          // start is the first axis day
        Assert.Equal(5, bar.SpanWorkingDays);        // Mon..Fri = 5 working days
        Assert.True(bar.HasStart);
    }

    [Fact] // The span counts only working days — a holiday inside the bar shortens it.
    public void Bar_span_excludes_internal_holiday()
    {
        var holidays = new HashSet<DateOnly> { new(2026, 6, 17) };
        var model = Build(holidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 19)), ScheduleState.Normal));

        var bar = Assert.Single(model.Bars);
        Assert.Equal(4, bar.SpanWorkingDays);        // 15,16,18,19 (Wed holiday dropped)
    }

    [Fact] // StartDayIndex is relative to the axis, not absolute — a later-starting bar is offset.
    public void StartDayIndex_offsets_into_the_shared_axis()
    {
        // Bar B starts two working days after bar A's start; the shared axis starts at A's start.
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 16), id: 1), ScheduleState.Normal),
            (Bl("B", start: new DateOnly(2026, 6, 17), internalDeadline: new DateOnly(2026, 6, 19), id: 2), ScheduleState.Normal));

        var a = model.Bars.Single(b => b.BacklogCode == "A");
        var b = model.Bars.Single(x => x.BacklogCode == "B");
        Assert.Equal(0, a.StartDayIndex);
        Assert.Equal(2, b.StartDayIndex);            // axis: 15(0),16(1),17(2),18(3),19(4)
        Assert.Equal(3, b.SpanWorkingDays);          // 17,18,19
    }

    [Fact] // ExternalMarkerIndex points at the axis position of the external (PCA) deadline.
    public void ExternalMarkerIndex_placed_on_axis()
    {
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 22),
                externalDeadline: new DateOnly(2026, 6, 18)), ScheduleState.Normal));

        var bar = Assert.Single(model.Bars);
        // axis: 15(0),16(1),17(2),18(3),19(4),22(5) → external 18 = index 3.
        Assert.Equal(3, bar.ExternalMarkerIndex);
    }

    [Fact] // No external deadline → no marker.
    public void No_external_deadline_means_null_marker()
    {
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), internalDeadline: new DateOnly(2026, 6, 19)), ScheduleState.Normal));

        Assert.Null(Assert.Single(model.Bars).ExternalMarkerIndex);
    }

    [Fact] // A no-start row → HasStart=false (drawn as a faint placeholder by the canvas).
    public void No_start_row_flagged_has_start_false()
    {
        var model = Build(NoHolidays,
            (Bl("A", start: null, internalDeadline: new DateOnly(2026, 6, 19)), ScheduleState.Late));

        var bar = Assert.Single(model.Bars);
        Assert.False(bar.HasStart);
        Assert.Equal(ScheduleState.Late, bar.ScheduleState);
    }

    [Fact] // Missing internal deadline but has end_date → span falls back to start→end.
    public void Falls_back_to_end_date_when_no_internal_deadline()
    {
        var model = Build(NoHolidays,
            (Bl("A", start: new DateOnly(2026, 6, 15), end: new DateOnly(2026, 6, 17)), ScheduleState.Normal));

        var bar = Assert.Single(model.Bars);
        Assert.True(bar.HasStart);
        Assert.Equal(0, bar.StartDayIndex);
        Assert.Equal(3, bar.SpanWorkingDays);        // 15,16,17
    }

    [Fact] // No rows / no dated rows → an empty model (guarded, no throw).
    public void Empty_source_yields_empty_model()
    {
        var empty = Build(NoHolidays);
        Assert.Empty(empty.Axis);
        Assert.Empty(empty.Bars);

        // A backlog with no dates at all contributes no axis bounds → empty model.
        var datelessOnly = Build(NoHolidays, (Bl("A"), ScheduleState.Normal));
        Assert.Empty(datelessOnly.Axis);
    }
}
