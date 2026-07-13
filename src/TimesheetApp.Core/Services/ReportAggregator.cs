using TimesheetApp.Models;

namespace TimesheetApp.Services;

/// <summary>The "DAYS LOGGED" stat: days that HAVE logs, over the working days of the week.</summary>
// M8.2: <see cref="WorkingDays"/> is the count of Mon–Fri days in the week that are not holidays — NOT
// the number of days that happen to carry a log. Those are different numbers, and conflating them is
// what made the stat unable to render anything but N/N.
public sealed record DaysLoggedStat(int Logged, int WorkingDays);

public interface IReportAggregator
{
    // RPT-01: one entry per DISTINCT WorkDate present in rows, ascending by date.
    IReadOnlyList<WeeklyDayTotal> WeeklyDayTotals(IReadOnlyList<TimeLogReportRow> rows);

    // M8.2: "3 / 5" = 3 of the week's 5 working days carry a log. The numerator counts day-totals with
    // hours; the denominator counts Mon–Fri minus holidays. Business arithmetic, so it lives here rather
    // than in ReportsViewModel — the Angular Reports screen (M8.7) inherits it instead of re-deriving it.
    DaysLoggedStat DaysLogged(
        IReadOnlyList<WeeklyDayTotal> dayTotals, DateOnly weekMonday, IReadOnlySet<DateOnly> holidays);

    // RPT-01 detail: one entry per (WorkDate, BacklogCode, TaskName), ordered by date then backlog then task.
    IReadOnlyList<WeeklyDetailRow> WeeklyDetailRows(IReadOnlyList<TimeLogReportRow> rows);

    // RPT-02: one entry per (BacklogCode, Project, TaskName), ordered by BacklogCode then TaskName.
    IReadOnlyList<MonthlyBacklogTaskTotal> MonthlyBacklogTaskTotals(IReadOnlyList<TimeLogReportRow> rows);

    // RPT-03 / TM-08: Team -> Project -> Backlog -> Task(by TaskId) -> Date drill-down with rolled-up
    // totals. A single-team report yields exactly one TeamNode root (collapses cleanly).
    IReadOnlyList<TeamNode> BuildProjectTree(IReadOnlyList<TimeLogReportRow> rows);
}

public sealed class ReportAggregator : IReportAggregator
{
    private readonly IWorkingDayCalculator _calc;

    // Optional ctor param keeps `new ReportAggregator()` (tests) working alongside the DI registration,
    // matching SmartInputService. WorkingDayCalculator is pure and stateless, so the default is safe.
    public ReportAggregator(IWorkingDayCalculator? calc = null) => _calc = calc ?? new WorkingDayCalculator();

    public IReadOnlyList<WeeklyDayTotal> WeeklyDayTotals(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => r.WorkDate)
            .OrderBy(g => g.Key)
            .Select(g => new WeeklyDayTotal(g.Key, g.Sum(r => r.Hours)))
            .ToList();

    public DaysLoggedStat DaysLogged(
        IReadOnlyList<WeeklyDayTotal> dayTotals, DateOnly weekMonday, IReadOnlySet<DateOnly> holidays)
    {
        var monday = DateHelpers.MondayOf(weekMonday);
        var logged = dayTotals.Count(d => d.TotalHours > 0m);
        // The week is Mon–Fri (the entry grid's span); weekends and holidays are not working days.
        var workingDays = _calc.CountWorkingDays(monday, monday.AddDays(4), holidays);
        return new DaysLoggedStat(logged, workingDays);
    }

    public IReadOnlyList<WeeklyDetailRow> WeeklyDetailRows(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => (r.WorkDate, r.BacklogCode, r.Project, r.TaskName))
            .OrderBy(g => g.Key.WorkDate)
            .ThenBy(g => g.Key.BacklogCode, StringComparer.Ordinal)
            .ThenBy(g => g.Key.TaskName, StringComparer.Ordinal)
            .Select(g => new WeeklyDetailRow(
                g.Key.WorkDate, g.Key.BacklogCode, g.Key.Project, g.Key.TaskName, g.Sum(r => r.Hours)))
            .ToList();

    public IReadOnlyList<MonthlyBacklogTaskTotal> MonthlyBacklogTaskTotals(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => (r.BacklogCode, r.Project, r.TaskName))
            .OrderBy(g => g.Key.BacklogCode, StringComparer.Ordinal)
            .ThenBy(g => g.Key.TaskName, StringComparer.Ordinal)
            .Select(g => new MonthlyBacklogTaskTotal(
                g.Key.BacklogCode, g.Key.Project, g.Key.TaskName, g.Sum(r => r.Hours)))
            .ToList();

    // v8 (P10): label for rows whose backlog has no team yet (pre-bootstrap). Sorts last under Ordinal.
    private const string NoTeamLabel = "(no team)";

    public IReadOnlyList<TeamNode> BuildProjectTree(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => r.TeamName ?? NoTeamLabel)
            .OrderBy(tm => tm.Key, StringComparer.Ordinal)
            .Select(tm => new TeamNode(
                tm.Key,
                tm.Sum(r => r.Hours),
                tm.GroupBy(r => r.Project)
                  .OrderBy(p => p.Key, StringComparer.Ordinal)
                  .Select(p => new ProjectNode(
                      p.Key,
                      p.Sum(r => r.Hours),
                      p.GroupBy(r => r.BacklogCode)
                       .OrderBy(rq => rq.Key, StringComparer.Ordinal)
                       .Select(rq => new BacklogNode(
                           rq.Key,
                           rq.First().Project,
                           rq.Sum(r => r.Hours),
                           // key tasks by surrogate id so two same-named soft-deleted tasks stay distinct (XC-06)
                           rq.GroupBy(r => r.TaskId)
                             .OrderBy(t => t.First().TaskName, StringComparer.Ordinal)
                             .Select(t => new TaskNode(
                                 t.Key,
                                 t.First().TaskName,
                                 t.Sum(r => r.Hours),
                                 t.GroupBy(r => r.WorkDate)
                                  .OrderBy(d => d.Key)
                                  .Select(d => new DateEntry(d.Key, d.Sum(r => r.Hours)))
                                  .ToList()))
                             .ToList()))
                       .ToList()))
                  .ToList()))
            .ToList();
}
