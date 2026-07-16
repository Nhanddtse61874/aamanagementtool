using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.Tests.Data;
using Xunit;

namespace TimesheetApp.Tests.Services;

// M9 (P1c): the server-side Task List read model.
//
// THESE TESTS USE THE REAL ScheduleStateService AND THE REAL WorkingDayCalculator over a real SQLite
// TestDb — deliberately. Mocking IScheduleStateService here would assert only that TaskListService
// calls something; it would pin no chip. The entire reason this read model exists is that the pace
// rule must have exactly ONE implementation, so the tests have to exercise the real one end-to-end.
// If a client ever reimplements this maths, THIS is the file it has to agree with.
public sealed class TaskListServiceTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private BacklogRepository _backlogs = null!;
    private TaskRepository _tasks = null!;
    private TimeLogRepository _timeLogs = null!;
    private TagRepository _tags = null!;
    private PcaContactRepository _pca = null!;
    private UserRepository _users = null!;
    private HolidayRepository _holidays = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        _backlogs = new BacklogRepository(_db);
        _tasks = new TaskRepository(_db);
        _timeLogs = new TimeLogRepository(_db);
        _tags = new TagRepository(_db);
        _pca = new PcaContactRepository(_db);
        _users = new UserRepository(_db);
        _holidays = new HolidayRepository(_db);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private sealed class FakeClock : IClock
    {
        public DateOnly Today { get; init; }
        public DateTimeOffset UtcNow => new(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private TaskListService Svc(DateOnly today) =>
        new(_backlogs, _tasks, _timeLogs, _tags, _pca, _users, _holidays,
            new WorkingDayCalculator(), new ScheduleStateService(), new FakeClock { Today = today });

    // ---- Anchors. All weekdays, so no accidental weekend arithmetic. ---------------------------------
    private static readonly DateOnly Today = new(2026, 6, 17);          // Wed
    private static readonly DateOnly Start = new(2026, 6, 15);          // Mon
    private static readonly DateOnly DeadlineNear = new(2026, 6, 19);   // Fri — (today,deadline] = 2 wd → IN the ≤2 window
    private static readonly DateOnly DeadlineFar = new(2026, 6, 30);    // Tue+ — far outside the window
    private static readonly DateOnly DeadlinePast = new(2026, 6, 15);   // Mon — already behind us
    private const string Month = "2026-06";

    private Task<int> SeedBacklogAsync(
        string code, string? month = Month, DateOnly? start = null, DateOnly? internalDeadline = null,
        decimal? rough = null, decimal? official = null,
        int? assigneeUserId = null, int? pcaContactId = null, int? teamId = null) =>
        _backlogs.InsertAsync(new Backlog(
            0, code, "ARCS", DateTimeOffset.UtcNow,
            StartDate: start, PeriodMonth: month,
            AssigneeUserId: assigneeUserId,
            DeadlineInternal: internalDeadline,
            RoughEstimateHours: rough, OfficialEstimateHours: official,
            PcaContactId: pcaContactId, TeamId: teamId));

    private async Task<int> SeedTaskAsync(int backlogId, string status = "Todo")
    {
        var id = await _db.SeedTaskAsync(backlogId, "T", 0);
        if (status != "Todo") await _tasks.UpdateStatusAsync(id, status);
        return id;
    }

    private async Task LogHoursAsync(int taskId, decimal hours, DateOnly? on = null)
    {
        var userId = await _db.SeedUserAsync("Logger" + Guid.NewGuid().ToString("N")[..6]);
        await _timeLogs.UpsertAsync(
            new TimeLog(0, userId, taskId, on ?? Start, hours, DateTimeOffset.UtcNow));
    }

    // GetRowsAsync takes a NON-nullable team list, and an EMPTY one means NO teams (R6) — so every test
    // seeds its backlogs into one team and loads scoped to it. SeedTeamAsync records that team here.
    private IReadOnlyList<int> _seededTeam = Array.Empty<int>();

    private async Task<int> SeedTeamAsync()
    {
        var id = await _db.SeedTeamAsync("Alpha");
        _seededTeam = new[] { id };
        return id;
    }

    private Task<TaskListScreen> LoadAsync(DateOnly today, int month = 6) =>
        Svc(today).GetRowsAsync(2026, month, _seededTeam);

    // =================================================================================================
    // THE CHIP. This is the contract a client cannot reproduce without logged-hours-per-backlog, which
    // no endpoint exposes — the reason this service exists.
    // =================================================================================================

    [Fact] // Past its internal deadline, not Done → Late.
    public async Task Past_internal_deadline_is_Late()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync("LATE-1", start: Start, internalDeadline: DeadlinePast, teamId: team);
        await SeedTaskAsync(id);   // a Todo task → not Done

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Late, row.ScheduleState);
    }

    // Behind pace, INSIDE the ≤2-working-day window → Warning.
    //   start Mon 15 → deadline Fri 19, today Wed 17.
    //   window: working days in (17, 19] = Thu 18, Fri 19 = 2 → in window.
    //   pace:   elapsed = wd(15..17) = 3 of total wd(15..19) = 5; logged 0 of estimate 10 → behind.
    [Fact]
    public async Task Behind_pace_inside_the_two_working_day_window_is_Warning()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync(
            "WARN-1", start: Start, internalDeadline: DeadlineNear, official: 10m, teamId: team);
        await SeedTaskAsync(id);   // Todo, and no hours logged → 0 of 10

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Warning, row.ScheduleState);
    }

    // The exact counterweight to the test above: SAME dates, SAME window, SAME estimate — only the
    // logged hours differ. behind ⟺ logged*total < elapsed*estimate ⟺ logged*5 < 3*10, so 6h is
    // precisely ON pace and must NOT warn. A client that got the formula even slightly wrong (used
    // calendar days, or `<=` for `<`, or forgot to clamp elapsed) flips one of these two tests.
    [Fact]
    public async Task On_pace_inside_the_window_is_Normal()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync(
            "PACE-1", start: Start, internalDeadline: DeadlineNear, official: 10m, teamId: team);
        var task = await SeedTaskAsync(id);
        await LogHoursAsync(task, 6m);   // 6*5 = 30, not < 3*10 = 30 → on pace

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Normal, row.ScheduleState);
    }

    [Fact] // Behind, but the deadline is far away → outside the window → no chip yet.
    public async Task Behind_but_outside_the_window_is_Normal()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync(
            "FAR-1", start: Start, internalDeadline: DeadlineFar, official: 10m, teamId: team);
        await SeedTaskAsync(id);   // 0 of 10 logged — behind, but not near the deadline

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Normal, row.ScheduleState);
    }

    // =================================================================================================
    // ZERO TASKS IS NOT DONE.
    //
    // `Enumerable.All` is TRUE on an empty sequence, so `tasks.All(t => t.Status == "Done")` alone marks
    // an EMPTY backlog Done — and Done short-circuits ScheduleState to Normal. The backlogs with no plan
    // yet are exactly the ones most likely to be slipping, and they would be the ones with no Late chip.
    // =================================================================================================

    [Fact] // Zero tasks → NOT Done → the schedule rule is still EVALUATED (past deadline ⇒ Late).
    public async Task Zero_tasks_is_not_Done_so_ScheduleState_is_still_evaluated()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("EMPTY-1", start: Start, internalDeadline: DeadlinePast, teamId: team);
        // NO tasks seeded at all.

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Empty(row.Tasks);
        Assert.Equal(ScheduleState.Late, row.ScheduleState);   // NOT Normal — it was not short-circuited
    }

    // The contrast that proves isDone genuinely flows (rather than the zero-task case passing because
    // isDone is simply always false): all tasks Done, same past deadline → Done DOES suppress the chip.
    [Fact]
    public async Task All_tasks_Done_suppresses_the_chip()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync("DONE-1", start: Start, internalDeadline: DeadlinePast, teamId: team);
        await SeedTaskAsync(id, status: "Done");
        await SeedTaskAsync(id, status: "Done");

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Normal, row.ScheduleState);
    }

    [Fact] // One task still open → not Done → Late stands.
    public async Task One_open_task_among_Done_ones_is_not_Done()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync("MIXED-1", start: Start, internalDeadline: DeadlinePast, teamId: team);
        await SeedTaskAsync(id, status: "Done");
        await SeedTaskAsync(id, status: "In-process");

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(ScheduleState.Late, row.ScheduleState);
    }

    // =================================================================================================
    // Projection + scoping.
    // =================================================================================================

    [Fact] // DEFAULT is the hidden catch-all backlog, not a work item — it must never be a row (DATA-03).
    public async Task Default_backlog_is_absent_from_the_result()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("DEFAULT", teamId: team);
        await SeedBacklogAsync("REAL-1", teamId: team);

        var screen = await LoadAsync(Today);

        Assert.Equal(new[] { "REAL-1" }, screen.Rows.Select(r => r.BacklogCode).ToArray());
        Assert.DoesNotContain(screen.Rows, r => r.BacklogCode == "DEFAULT");
        Assert.DoesNotContain(screen.Gantt.Bars, b => b.BacklogCode == "DEFAULT");   // nor a bar for it
    }

    [Fact] // A concrete month filters to that period; other months and null-period backlogs drop out.
    public async Task A_concrete_month_filters_to_that_period()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("JUN-1", month: "2026-06", teamId: team);
        await SeedBacklogAsync("JUL-1", month: "2026-07", teamId: team);
        await SeedBacklogAsync("NOPER", month: null, teamId: team);

        var screen = await LoadAsync(Today, month: 6);

        Assert.Equal(new[] { "JUN-1" }, screen.Rows.Select(r => r.BacklogCode).ToArray());
    }

    [Fact] // month == 0 is the "All months" sentinel: every backlog, any period, including a null one.
    public async Task Month_zero_lists_every_backlog_including_null_period()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("JUN-1", month: "2026-06", teamId: team);
        await SeedBacklogAsync("JUL-1", month: "2026-07", teamId: team);
        await SeedBacklogAsync("NOPER", month: null, teamId: team);

        var screen = await LoadAsync(Today, month: 0);

        // Already ordered by code by the service — JUL sorts before JUN ('L' < 'N').
        Assert.Equal(new[] { "JUL-1", "JUN-1", "NOPER" },
            screen.Rows.Select(r => r.BacklogCode).ToArray());
    }

    [Fact] // Rows come back ordered by code (case-insensitive), like the grid renders them.
    public async Task Rows_are_ordered_by_backlog_code()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("charlie", teamId: team);
        await SeedBacklogAsync("Alpha", teamId: team);
        await SeedBacklogAsync("bravo", teamId: team);

        var screen = await LoadAsync(Today);

        Assert.Equal(new[] { "Alpha", "bravo", "charlie" }, screen.Rows.Select(r => r.BacklogCode).ToArray());
    }

    // R6, and a sharp edge worth pinning: an EMPTY team list means NO teams, not "all teams". A caller
    // that passes an empty list expecting "don't filter" gets an empty screen, by design.
    [Fact]
    public async Task An_empty_team_list_yields_no_rows()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("REAL-1", teamId: team);

        var screen = await Svc(Today).GetRowsAsync(2026, 6, Array.Empty<int>());

        Assert.Empty(screen.Rows);
        Assert.Empty(screen.Gantt.Bars);
    }

    [Fact] // Another team's backlog is not visible.
    public async Task Rows_are_scoped_to_the_requested_teams()
    {
        var mine = await _db.SeedTeamAsync("Mine");
        var theirs = await _db.SeedTeamAsync("Theirs");
        await SeedBacklogAsync("MINE-1", teamId: mine);
        await SeedBacklogAsync("THEIRS-1", teamId: theirs);

        var screen = await Svc(Today).GetRowsAsync(2026, 6, new[] { mine });

        Assert.Equal(new[] { "MINE-1" }, screen.Rows.Select(r => r.BacklogCode).ToArray());
    }

    [Fact] // TL-12: the owning team id rides the row — projected straight off the backlog, no join.
    public async Task Row_carries_the_owning_teamId()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("TEAM-1", teamId: team);

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(team, row.TeamId);
    }

    // =================================================================================================
    // The roll-up + the resolved names + the Gantt.
    // =================================================================================================

    // Logged hours are ALL-TIME (A1), not month-scoped: hours logged in a different month still count
    // toward the backlog's roll-up. Also proves the batched dictionary is keyed/summed correctly.
    [Fact]
    public async Task Logged_hours_are_all_time_and_summed_per_backlog()
    {
        var team = await SeedTeamAsync();
        var id = await SeedBacklogAsync("SUM-1", teamId: team);
        var t1 = await SeedTaskAsync(id);
        var t2 = await SeedTaskAsync(id);

        await LogHoursAsync(t1, 3m, on: new DateOnly(2026, 6, 16));
        await LogHoursAsync(t2, 2m, on: new DateOnly(2026, 6, 16));
        await LogHoursAsync(t1, 4m, on: new DateOnly(2026, 1, 20));   // a DIFFERENT month — still counts

        var row = Assert.Single((await LoadAsync(Today)).Rows);
        Assert.Equal(9m, row.LoggedHours);
    }

    [Fact] // §7 precedence: the official estimate wins when present, else the rough one.
    public async Task Estimate_is_official_then_rough()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("EST-1", rough: 5m, official: 12m, teamId: team);
        await SeedBacklogAsync("EST-2", rough: 7m, teamId: team);
        await SeedBacklogAsync("EST-3", teamId: team);

        var rows = (await LoadAsync(Today)).Rows.ToDictionary(r => r.BacklogCode);

        Assert.Equal(12m, rows["EST-1"].EstimateHours);   // official wins
        Assert.Equal(7m, rows["EST-2"].EstimateHours);    // falls back to rough
        Assert.Null(rows["EST-3"].EstimateHours);         // neither → null, not 0
    }

    [Fact] // PCT / PCA names AND ids resolve, and tags come back ordered by tag id (Q4).
    public async Task Names_ids_and_tags_resolve_on_the_row()
    {
        var team = await SeedTeamAsync();
        var userId = await _db.SeedUserAsync("Ada", "ada");
        var pcaId = await _pca.InsertAsync(new PcaContact(0, "Grace", true));
        var tagB = await _tags.InsertAsync(new Tag(0, "beta", "B", "#111111", DateTimeOffset.UtcNow));
        var tagA = await _tags.InsertAsync(new Tag(0, "alpha", "A", "#222222", DateTimeOffset.UtcNow));

        var id = await SeedBacklogAsync("NAMES-1", assigneeUserId: userId, pcaContactId: pcaId, teamId: team);
        await _backlogs.SetTagsAsync(id, new[] { tagA, tagB });

        var row = Assert.Single((await LoadAsync(Today)).Rows);

        Assert.Equal("Ada", row.PctAssigneeName);
        Assert.Equal("Grace", row.PcaContactName);
        // 🔴 The ids SEED the inline PCT/PCA dropdowns — the name alone leaves a <select> with nothing to
        //    preselect. Projected straight off the Backlog entity the service already read.
        Assert.Equal(userId, row.AssigneeUserId);
        Assert.Equal(pcaId, row.PcaContactId);
        // Ordered by Tag.Id (tagB was inserted first, so it has the lower id) — not by text.
        Assert.Equal(new[] { tagB, tagA }, row.Tags.Select(t => t.Id).ToArray());
    }

    [Fact] // No assignee / PCA on the backlog → the ids are NULL (not 0) so the dropdown shows "—".
    public async Task Assignee_and_pca_ids_are_null_when_the_backlog_has_none()
    {
        var team = await SeedTeamAsync();
        await SeedBacklogAsync("NOIDS-1", teamId: team);   // no assignee, no PCA

        var row = Assert.Single((await LoadAsync(Today)).Rows);

        Assert.Null(row.AssigneeUserId);
        Assert.Null(row.PcaContactId);
        Assert.Null(row.PctAssigneeName);
        Assert.Null(row.PcaContactName);
    }

    // The snapshot guarantee, and the reason Rows + Gantt are ONE record rather than two calls: a bar's
    // colour is the SAME ScheduleState value as its row's chip. Fetched separately, a write landing
    // between the two calls could let the chart and the grid disagree on screen.
    [Fact]
    public async Task Gantt_bars_carry_the_same_ScheduleState_as_their_rows()
    {
        var team = await SeedTeamAsync();
        var late = await SeedBacklogAsync("A-LATE", start: Start, internalDeadline: DeadlinePast, teamId: team);
        await SeedTaskAsync(late);
        var warn = await SeedBacklogAsync(
            "B-WARN", start: Start, internalDeadline: DeadlineNear, official: 10m, teamId: team);
        await SeedTaskAsync(warn);

        var screen = await LoadAsync(Today);

        Assert.Equal(ScheduleState.Late, screen.Rows.Single(r => r.BacklogCode == "A-LATE").ScheduleState);
        Assert.Equal(ScheduleState.Warning, screen.Rows.Single(r => r.BacklogCode == "B-WARN").ScheduleState);

        foreach (var row in screen.Rows)
        {
            var bar = screen.Gantt.Bars.Single(b => b.BacklogId == row.BacklogId);
            Assert.Equal(row.ScheduleState, bar.ScheduleState);
        }
    }

    [Fact] // No backlogs in scope → an empty screen, not a throw and not a null Gantt.
    public async Task No_backlogs_yields_an_empty_screen()
    {
        await SeedTeamAsync();

        var screen = await LoadAsync(Today);

        Assert.Empty(screen.Rows);
        Assert.NotNull(screen.Gantt);
        Assert.Empty(screen.Gantt.Axis);
        Assert.Empty(screen.Gantt.Bars);
    }
}
