using TimesheetApp.Models;

namespace TimesheetApp.Services;

public interface IReportAggregator
{
    // RPT-01: one entry per DISTINCT WorkDate present in rows, ascending by date.
    IReadOnlyList<WeeklyDayTotal> WeeklyDayTotals(IReadOnlyList<TimeLogReportRow> rows);

    // RPT-01 detail: one entry per (WorkDate, BacklogCode, TaskName), ordered by date then backlog then task.
    IReadOnlyList<WeeklyDetailRow> WeeklyDetailRows(IReadOnlyList<TimeLogReportRow> rows);

    // RPT-02: one entry per (BacklogCode, Project, TaskName), ordered by BacklogCode then TaskName.
    IReadOnlyList<MonthlyBacklogTaskTotal> MonthlyBacklogTaskTotals(IReadOnlyList<TimeLogReportRow> rows);

    // RPT-03: Project -> Backlog -> Task(by TaskId) -> Date drill-down with rolled-up totals.
    IReadOnlyList<ProjectNode> BuildProjectTree(IReadOnlyList<TimeLogReportRow> rows);
}

public sealed class ReportAggregator : IReportAggregator
{
    public IReadOnlyList<WeeklyDayTotal> WeeklyDayTotals(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => r.WorkDate)
            .OrderBy(g => g.Key)
            .Select(g => new WeeklyDayTotal(g.Key, g.Sum(r => r.Hours)))
            .ToList();

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

    public IReadOnlyList<ProjectNode> BuildProjectTree(IReadOnlyList<TimeLogReportRow> rows) =>
        rows.GroupBy(r => r.Project)
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
            .ToList();
}
