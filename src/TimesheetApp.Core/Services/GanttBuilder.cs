using TimesheetApp.Models;

namespace TimesheetApp.Services;

// M9 (P1a): MOVED VERBATIM out of TaskListViewModel (WPF) — behaviour unchanged. It was already
// `internal static` and closed over nothing, and every type it touches (Backlog, ScheduleState,
// GanttModel/GanttBar, IWorkingDayCalculator) was already in Core; the only thing keeping it in the
// desktop assembly was where it happened to be typed. The web Task List needs the same bars, and a
// second implementation would be a second source of truth for the geometry.
//
// Stays `internal`: Core grants InternalsVisibleTo to BOTH TimesheetApp (the WPF call site) and
// TimesheetApp.Tests (the test), and TaskListService sits in this assembly — so nothing needs it
// widened, and `public` would put geometry math on Core's outward contract for no caller.
internal static class GanttBuilder
{
    /// <summary>
    /// Pure index math (no pixels — code-behind owns layout). Axis = the working days (weekends/holidays
    /// excluded) spanning min(start, fallback deadline) .. max(internal deadline / end). Per backlog a
    /// GanttBar: span = start → deadline_internal (Q3); missing internal but has end_date → start → end
    /// (neutral); no start_date → HasStart=false placeholder (still drawn, deadline-only). ExternalMarkerIndex
    /// = the axis position nearest deadline_external. Deterministic + unit-testable.
    /// </summary>
    internal static GanttModel BuildGantt(
        IReadOnlyList<(Backlog Backlog, ScheduleState State)> source,
        IReadOnlySet<DateOnly> holidays, IWorkingDayCalculator calc)
    {
        var empty = new GanttModel(Array.Empty<DateOnly>(), Array.Empty<GanttBar>());
        if (source.Count == 0) return empty;

        // The end of a bar drives the axis upper bound: internal deadline, else end_date (neutral fallback).
        static DateOnly? BarEnd(Backlog b) => b.DeadlineInternal ?? b.EndDate;
        // The start of a bar: the explicit start_date, else the deadline (a no-start placeholder bar).
        static DateOnly? BarStart(Backlog b) => b.StartDate ?? BarEnd(b);

        // Axis bounds across every backlog that contributes any date.
        DateOnly? min = null, max = null;
        foreach (var (b, _) in source)
        {
            if (BarStart(b) is { } sv && (min is not { } mv || sv < mv)) min = sv;
            if (BarEnd(b) is { } ev && (max is not { } xv || ev > xv)) max = ev;
        }
        if (min is not { } from || max is not { } to || from > to) return empty;

        var axis = calc.WorkingDaysBetween(from, to, holidays);
        if (axis.Count == 0) return empty;

        // index of the working day on/after `d` (clamped into [0, axis.Count-1]); axis is ascending.
        int NearestIndex(DateOnly d)
        {
            for (var i = 0; i < axis.Count; i++)
                if (axis[i] >= d) return i;
            return axis.Count - 1;
        }

        var bars = new List<GanttBar>(source.Count);
        foreach (var (b, state) in source)
        {
            var end = BarEnd(b);
            var start = BarStart(b);
            var hasStart = b.StartDate is not null;

            int startIdx, span;
            if (start is { } sv && end is { } ev)
            {
                startIdx = NearestIndex(sv);
                // Working-day count over the span, mapped to axis positions so weekends/holidays
                // inside the range are excluded (≥1 so even a same-day bar is visible).
                var endIdx = NearestIndex(ev);
                span = Math.Max(1, endIdx - startIdx + 1);
            }
            else
            {
                // No dates at all on this backlog → a zero-width placeholder pinned to the axis start.
                startIdx = 0;
                span = 0;
            }

            int? externalIdx = b.DeadlineExternal is { } ext ? NearestIndex(ext) : null;

            bars.Add(new GanttBar(
                b.Id, b.BacklogCode, b.StartDate, end, startIdx, span, externalIdx, hasStart, state));
        }

        return new GanttModel(axis, bars);
    }
}
