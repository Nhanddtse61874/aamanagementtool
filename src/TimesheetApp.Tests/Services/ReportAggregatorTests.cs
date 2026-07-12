using TimesheetApp.Models;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Services;

public class ReportAggregatorTests
{
    private readonly IReportAggregator _agg = new ReportAggregator();

    private static TimeLogReportRow Row(
        string project, string reqCode, int taskId, string taskName,
        string date, decimal hours, int userId = 1, string userName = "A",
        int? teamId = null, string? teamName = null) =>
        new(userId, userName, reqCode, project, taskId, taskName, DateOnly.Parse(date), hours, teamId, teamName);

    // RPT-01
    [Fact]
    public void WeeklyDayTotals_sums_hours_per_day_ascending()
    {
        var rows = new[]
        {
            Row("ProjX", "REQ-001", 10, "Implement", "2026-06-16", 4m),
            Row("ProjX", "REQ-001", 11, "Review",    "2026-06-16", 3m),
            Row("ProjX", "REQ-001", 10, "Implement", "2026-06-17", 5m),
        };

        var result = _agg.WeeklyDayTotals(rows);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateOnly(2026, 6, 16), result[0].Date);
        Assert.Equal(7m, result[0].TotalHours);   // 4 + 3
        Assert.Equal(new DateOnly(2026, 6, 17), result[1].Date);
        Assert.Equal(5m, result[1].TotalHours);
    }

    // RPT-02
    [Fact]
    public void MonthlyBacklogTaskTotals_groups_by_request_and_task_ordered()
    {
        var rows = new[]
        {
            Row("ProjX", "REQ-002", 20, "Spec", "2026-06-02", 2m),
            Row("ProjX", "REQ-001", 10, "Build","2026-06-03", 4m),
            Row("ProjX", "REQ-001", 10, "Build","2026-06-04", 1.5m),
            Row("ProjX", "REQ-001", 11, "Audit","2026-06-05", 2m),
        };

        var result = _agg.MonthlyBacklogTaskTotals(rows);

        Assert.Equal(3, result.Count);
        // ordered: REQ-001/Audit, REQ-001/Build, REQ-002/Spec
        Assert.Equal(("REQ-001", "Audit", 2m), (result[0].BacklogCode, result[0].TaskName, result[0].TotalHours));
        Assert.Equal(("REQ-001", "Build", 5.5m), (result[1].BacklogCode, result[1].TaskName, result[1].TotalHours));
        Assert.Equal(("REQ-002", "Spec", 2m), (result[2].BacklogCode, result[2].TaskName, result[2].TotalHours));
    }

    // RPT-03
    [Fact]
    public void BuildProjectTree_rolls_up_totals_at_every_level()
    {
        var rows = new[]
        {
            Row("ProjX", "REQ-001", 10, "Build", "2026-06-16", 4m),
            Row("ProjX", "REQ-001", 10, "Build", "2026-06-17", 2m),
            Row("ProjX", "REQ-001", 11, "Audit", "2026-06-16", 1m),
            Row("ProjY", "REQ-002", 20, "Spec",  "2026-06-16", 3m),
        };

        var tree = _agg.BuildProjectTree(rows);

        // Single (no-team) root collapses to one TeamNode; assert the project level beneath it.
        var projects = Assert.Single(tree).Projects;
        Assert.Equal(2, projects.Count);
        var projX = projects.Single(p => p.Project == "ProjX");
        Assert.Equal(7m, projX.TotalHours);                 // 4 + 2 + 1
        var req1 = projX.Backlogs.Single();
        Assert.Equal("REQ-001", req1.BacklogCode);
        Assert.Equal(7m, req1.TotalHours);
        var build = req1.Tasks.Single(t => t.TaskName == "Build");
        Assert.Equal(6m, build.TotalHours);                 // 4 + 2
        Assert.Equal(2, build.Dates.Count);                 // 16th, 17th
        Assert.Equal(new DateOnly(2026, 6, 16), build.Dates[0].Date);
        Assert.Equal(4m, build.Dates[0].TotalHours);

        var projY = projects.Single(p => p.Project == "ProjY");
        Assert.Equal(3m, projY.TotalHours);
    }

    // RPT-03 / TM-08 — a multi-team report gets a Team root level above Project; single team => one root.
    [Fact]
    public void BuildProjectTree_adds_team_root_level()
    {
        var rows = new[]
        {
            Row("ProjX", "REQ-001", 10, "Build", "2026-06-16", 4m, teamId: 1, teamName: "Team A"),
            Row("ProjY", "REQ-002", 20, "Spec",  "2026-06-16", 3m, teamId: 2, teamName: "Team B"),
            Row("ProjX", "REQ-003", 30, "Audit", "2026-06-17", 1m, teamId: 1, teamName: "Team A"),
        };

        var tree = _agg.BuildProjectTree(rows);

        Assert.Equal(2, tree.Count);                                  // two team roots
        var teamA = tree.Single(t => t.TeamName == "Team A");
        Assert.Equal(5m, teamA.TotalHours);                          // 4 + 1, both ProjX
        Assert.Equal("ProjX", Assert.Single(teamA.Projects).Project);
        var teamB = tree.Single(t => t.TeamName == "Team B");
        Assert.Equal(3m, teamB.TotalHours);
        Assert.Equal("ProjY", Assert.Single(teamB.Projects).Project);
    }

    [Fact]
    public void BuildProjectTree_single_team_collapses_to_one_root()
    {
        var rows = new[]
        {
            Row("ProjX", "REQ-001", 10, "Build", "2026-06-16", 4m, teamId: 1, teamName: "Team A"),
            Row("ProjY", "REQ-002", 20, "Spec",  "2026-06-16", 3m, teamId: 1, teamName: "Team A"),
        };

        var tree = _agg.BuildProjectTree(rows);

        var root = Assert.Single(tree);
        Assert.Equal("Team A", root.TeamName);
        Assert.Equal(7m, root.TotalHours);
        Assert.Equal(2, root.Projects.Count);
    }

    // RPT-03 / XC-06 — soft-deleted task NAMES still resolve, and two same-named tasks
    // (e.g. an old soft-deleted "Annual Leave" + a renamed-in new one) stay DISTINCT by TaskId.
    [Fact]
    public void BuildProjectTree_keeps_soft_deleted_task_names_distinct_by_id()
    {
        // The repo join does NOT filter is_active, so a soft-deleted task's rows arrive here
        // with its (still-resolved) name. Two tasks share the name "Annual Leave" but differ by id.
        var rows = new[]
        {
            Row("DEFAULT", "DEFAULT", 99, "Annual Leave", "2026-06-10", 8m), // old (soft-deleted) task id 99
            Row("DEFAULT", "DEFAULT", 42, "Annual Leave", "2026-06-12", 8m), // new task id 42
        };

        var tree = _agg.BuildProjectTree(rows);

        var defaultReq = tree.Single().Projects.Single().Backlogs.Single();
        Assert.Equal(2, defaultReq.Tasks.Count);                       // distinct by id, not collapsed by name
        Assert.All(defaultReq.Tasks, t => Assert.Equal("Annual Leave", t.TaskName));
        Assert.Equal(new[] { 42, 99 }, defaultReq.Tasks.Select(t => t.TaskId).OrderBy(x => x));
        Assert.Equal(16m, defaultReq.TotalHours);                      // both still counted (names resolved)
    }

    [Fact]
    public void Empty_input_yields_empty_projections()
    {
        var empty = System.Array.Empty<TimeLogReportRow>();
        Assert.Empty(_agg.WeeklyDayTotals(empty));
        Assert.Empty(_agg.MonthlyBacklogTaskTotals(empty));
        Assert.Empty(_agg.BuildProjectTree(empty));
    }

    // ---- M8.2: DAYS LOGGED (moved out of ReportsViewModel) ----
    //
    // The denominator is the number of WORKING days in the week, NOT the number of days that carry a
    // log. ReportsViewModel used WeeklyRows.Count for both, so the stat could only ever read N/N.

    private static readonly DateOnly Monday = new(2026, 6, 15);   // Mon 15 .. Fri 19
    private static readonly IReadOnlySet<DateOnly> NoHolidays = new HashSet<DateOnly>();

    [Fact]
    public void DaysLogged_is_logged_days_over_working_days_not_over_logged_days()
    {
        var totals = new[]
        {
            new WeeklyDayTotal(Monday, 8m),
            new WeeklyDayTotal(Monday.AddDays(1), 8m),
            new WeeklyDayTotal(Monday.AddDays(2), 8m),
        };

        var stat = _agg.DaysLogged(totals, Monday, NoHolidays);

        Assert.Equal(3, stat.Logged);
        Assert.Equal(5, stat.WorkingDays);   // the old code produced 3 here, so it could only show 3/3
    }

    [Fact]
    public void DaysLogged_drops_holidays_from_the_denominator()
    {
        var holidays = new HashSet<DateOnly> { Monday.AddDays(2) };   // Wed is a public holiday
        var totals = new[] { new WeeklyDayTotal(Monday, 8m) };

        var stat = _agg.DaysLogged(totals, Monday, holidays);

        Assert.Equal(1, stat.Logged);
        Assert.Equal(4, stat.WorkingDays);   // Mon, Tue, Thu, Fri — a holiday is not a working day
    }

    [Fact]
    public void DaysLogged_with_no_logs_still_reports_the_full_working_week()
    {
        var stat = _agg.DaysLogged(Array.Empty<WeeklyDayTotal>(), Monday, NoHolidays);

        Assert.Equal(0, stat.Logged);
        Assert.Equal(5, stat.WorkingDays);   // "0 / 5", not "0 / 0"
    }

    [Fact]
    public void DaysLogged_ignores_zero_hour_days_in_the_numerator()
    {
        var totals = new[]
        {
            new WeeklyDayTotal(Monday, 8m),
            new WeeklyDayTotal(Monday.AddDays(1), 0m),   // present but empty -> not "logged"
        };

        var stat = _agg.DaysLogged(totals, Monday, NoHolidays);

        Assert.Equal(1, stat.Logged);
        Assert.Equal(5, stat.WorkingDays);
    }
}
