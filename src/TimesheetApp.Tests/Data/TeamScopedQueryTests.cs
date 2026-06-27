using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.Tests.Data;

// P10: team-dimension query behavior on the real v8 schema — the SearchAsync/export leak guards (R1),
// per-team DEFAULT lookup (R2), and the active-team working-scope filter (TM-06).
public class TeamScopedQueryTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _backlogs = null!;
    private TaskRepository _tasks = null!;
    private TimeLogRepository _logs = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _backlogs = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _logs = new TimeLogRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    // ---- BacklogRepository.SearchAsync team filter ----

    [Fact]
    public async Task SearchAsync_null_teamIds_returns_all_teams()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var teamB = await _db.SeedTeamAsync("B");
        await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        await _backlogs.InsertAsync(new Backlog(0, "B-1", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));

        var all = await _backlogs.SearchAsync(null);   // null => no team filter
        Assert.Contains(all, b => b.BacklogCode == "A-1");
        Assert.Contains(all, b => b.BacklogCode == "B-1");
    }

    [Fact]
    public async Task SearchAsync_filters_to_checked_teams_only()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var teamB = await _db.SeedTeamAsync("B");
        await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        await _backlogs.InsertAsync(new Backlog(0, "B-1", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));

        var onlyA = await _backlogs.SearchAsync(null, new[] { teamA });
        Assert.Contains(onlyA, b => b.BacklogCode == "A-1");
        Assert.DoesNotContain(onlyA, b => b.BacklogCode == "B-1");   // no leak
    }

    [Fact]
    public async Task SearchAsync_empty_teamIds_returns_nothing()  // teamId 0 == empty (R6)
    {
        var teamA = await _db.SeedTeamAsync("A");
        await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));

        var none = await _backlogs.SearchAsync(null, Array.Empty<int>());
        Assert.Empty(none);
    }

    [Fact]
    public async Task SearchAsync_term_and_team_filter_combine()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var teamB = await _db.SeedTeamAsync("B");
        await _backlogs.InsertAsync(new Backlog(0, "SHARED", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        await _backlogs.InsertAsync(new Backlog(0, "SHARED", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));

        var hits = await _backlogs.SearchAsync("SHARED", new[] { teamB });
        Assert.Single(hits);
        Assert.Equal("ARMS", hits[0].Project);   // only team B's SHARED
    }

    // ---- GetDefaultForTeamAsync (per-team DEFAULT, R2) ----

    [Fact]
    public async Task GetDefaultForTeam_resolves_only_that_teams_default()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var teamB = await _db.SeedTeamAsync("B");
        var defA = await _backlogs.InsertAsync(new Backlog(0, "DEFAULT", "DEFAULT", DateTimeOffset.UtcNow, TeamId: teamA));
        await _backlogs.InsertAsync(new Backlog(0, "DEFAULT", "DEFAULT", DateTimeOffset.UtcNow, TeamId: teamB));

        var resolved = await _backlogs.GetDefaultForTeamAsync(teamA);
        Assert.Equal(defA, resolved!.Id);
        Assert.Equal(teamA, resolved.TeamId);

        // A team with no DEFAULT yet returns null.
        var teamC = await _db.SeedTeamAsync("C");
        Assert.Null(await _backlogs.GetDefaultForTeamAsync(teamC));
    }

    [Fact]
    public async Task Insert_then_GetById_roundtrips_team_id()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var id = await _backlogs.InsertAsync(new Backlog(0, "TT-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        Assert.Equal(teamA, (await _backlogs.GetByIdAsync(id))!.TeamId);
    }

    // ---- TaskRepository.GetActiveForTimesheetAsync(teamId) working scope (TM-06) ----

    [Fact]
    public async Task GetActiveForTimesheet_scopes_to_the_active_team()
    {
        var teamA = await _db.SeedTeamAsync("A");
        var teamB = await _db.SeedTeamAsync("B");
        var blA = await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        var blB = await _backlogs.InsertAsync(new Backlog(0, "B-1", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));
        await _tasks.InsertAsync(new TaskItem(0, blA, "Task A", 0, true));
        await _tasks.InsertAsync(new TaskItem(0, blB, "Task B", 0, true));

        var scopedA = await _tasks.GetActiveForTimesheetAsync(teamA);
        Assert.Contains(scopedA, t => t.TaskName == "Task A");
        Assert.DoesNotContain(scopedA, t => t.TaskName == "Task B");   // other team's tasks excluded

        // null => all teams (legacy behavior preserved, R6).
        var all = await _tasks.GetActiveForTimesheetAsync();
        Assert.Contains(all, t => t.TaskName == "Task A");
        Assert.Contains(all, t => t.TaskName == "Task B");
    }

    [Fact]
    public async Task GetActiveForTimesheet_teamId_zero_returns_nothing()  // teamId 0 == empty (R6)
    {
        var teamA = await _db.SeedTeamAsync("A");
        var blA = await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        await _tasks.InsertAsync(new TaskItem(0, blA, "Task A", 0, true));

        Assert.Empty(await _tasks.GetActiveForTimesheetAsync(0));
    }

    // ---- TimeLogRepository export/report leak guard + team projection (R1) ----

    [Fact]
    public async Task GetExportRows_filters_by_team_and_projects_team_name()
    {
        var teamA = await _db.SeedTeamAsync("Alpha");
        var teamB = await _db.SeedTeamAsync("Bravo");
        var userId = await _db.SeedUserAsync("Logger");
        var blA = await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        var blB = await _backlogs.InsertAsync(new Backlog(0, "B-1", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));
        var taskA = await _tasks.InsertAsync(new TaskItem(0, blA, "Task A", 0, true));
        var taskB = await _tasks.InsertAsync(new TaskItem(0, blB, "Task B", 0, true));
        var d = new DateOnly(2026, 6, 16);
        await _logs.UpsertAsync(new TimeLog(0, userId, taskA, d, 4m, DateTimeOffset.UtcNow));
        await _logs.UpsertAsync(new TimeLog(0, userId, taskB, d, 5m, DateTimeOffset.UtcNow));

        // null => every team's rows (leak by default; the caller must scope).
        var unscoped = await _logs.GetExportRowsAsync(d, d, null);
        Assert.Equal(2, unscoped.Count);

        // Scoped to team A only — team B's row must NOT leak (R1).
        var scoped = await _logs.GetExportRowsAsync(d, d, null, new[] { teamA });
        var row = Assert.Single(scoped);
        Assert.Equal("Task A", row.TaskName);
        Assert.Equal(teamA, row.TeamId);
        Assert.Equal("Alpha", row.TeamName);   // team name projected (LEFT JOIN Teams)
    }

    [Fact]
    public async Task GetReportRows_filters_by_team()
    {
        var teamA = await _db.SeedTeamAsync("Alpha");
        var teamB = await _db.SeedTeamAsync("Bravo");
        var userId = await _db.SeedUserAsync("Logger");
        var blA = await _backlogs.InsertAsync(new Backlog(0, "A-1", "ARCS", DateTimeOffset.UtcNow, TeamId: teamA));
        var blB = await _backlogs.InsertAsync(new Backlog(0, "B-1", "ARMS", DateTimeOffset.UtcNow, TeamId: teamB));
        var taskA = await _tasks.InsertAsync(new TaskItem(0, blA, "Task A", 0, true));
        var taskB = await _tasks.InsertAsync(new TaskItem(0, blB, "Task B", 0, true));
        var d = new DateOnly(2026, 6, 16);
        await _logs.UpsertAsync(new TimeLog(0, userId, taskA, d, 4m, DateTimeOffset.UtcNow));
        await _logs.UpsertAsync(new TimeLog(0, userId, taskB, d, 5m, DateTimeOffset.UtcNow));

        var scoped = await _logs.GetReportRowsAsync(userId, d, d, new[] { teamB });
        var row = Assert.Single(scoped);
        Assert.Equal("Task B", row.TaskName);
        Assert.Equal("Bravo", row.TeamName);
    }
}
