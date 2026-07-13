using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>W2-B. Covers <c>/api/timesheet/*</c>, <c>/api/smartfill/*</c>, <c>/api/reports/*</c> and
/// <c>/api/export/*</c> -- the only Wave-2 area that writes hours, and the only one where the "deleted"
/// half of the 409 contract is reachable (see
/// <see cref="A_stale_cell_save_against_a_deleted_cell_is_409_with_deleted_true"/>): a TimeLogs cell has no
/// id -- its key is the natural (user_id, task_id, work_date) triple -- so the rule-#8 authorization
/// pre-read is on the TASK, which survives the cell's own deletion, unlike Backlogs/Tasks where the same
/// pre-read shadows that path entirely (see <c>BacklogEndpointsTests</c>).</summary>
public sealed class TimesheetEndpointsTests
{
    // ==== Timesheet: week grid + checked cell writes =========================================================

    [Fact]
    public async Task A_saved_cell_appears_in_the_callers_week_grid()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();

        var save = await SaveCellAsync(client, taskId, monday, 3m, expectedVersion: null);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var week = await client.GetAsync($"/api/timesheet/week?monday={monday:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, week.StatusCode);
        var groups = await week.Content.ReadFromJsonAsync<List<WeekBacklogGroup>>();
        var row = groups!.SelectMany(g => g.Tasks).Single(t => t.TaskId == taskId);
        Assert.Equal(3m, row.Mon.Hours);
    }

    [Fact]
    public async Task Team_view_of_the_week_grid_aggregates_hours_across_users()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var carolId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, carolId);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-TEAM");
        var taskId = await factory.SeedTaskAsync(backlogId, "Shared task");
        var monday = AMonday();

        var aliceClient = await factory.ClientAsync("alice");
        var carolClient = await factory.ClientAsync("carol");
        Assert.Equal(HttpStatusCode.OK, (await SaveCellAsync(aliceClient, taskId, monday, 2m, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveCellAsync(carolClient, taskId, monday, 3m, null)).StatusCode);

        var response = await aliceClient.GetAsync($"/api/timesheet/week?monday={monday:yyyy-MM-dd}&allUsers=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<List<WeekBacklogGroup>>();
        var row = groups!.SelectMany(g => g.Tasks).Single(t => t.TaskId == taskId);
        Assert.Equal(5m, row.Mon.Hours); // summed across BOTH users, not just the caller
    }

    // ==== M8.4: the read->write version loop, over real HTTP ==================================================

    /// <summary>THE M8.4 ROUND TRIP, and the reason the milestone exists. <c>GET /api/timesheet/week</c> is the
    /// only timesheet read route there is, and it used to return hours with NO versions — the projection in
    /// <c>TimeLogService</c> fetched TimeLogs that carry <c>row_version</c> and then dropped it. So on a fresh
    /// page load the client had hours and nothing to put in <c>expectedVersion</c>. Its only two options were
    /// both broken: send <c>null</c>, which deliberately asserts "I believe this cell is EMPTY" and so 409s
    /// against the existing row (every edit of a pre-existing cell, every user, forever); or delete the version
    /// check and silently destroy the optimistic concurrency M8.2 spent five waves building.
    ///
    /// <para>This closes the loop end to end: READ the week -> take the cell's <c>rowVersion</c> -> PUT it back
    /// as <c>expectedVersion</c> -> 200, version advanced by exactly one -> PUT again at the now-STALE version
    /// -> 409. Both halves matter: the 200 proves the version the read hands out is ACCEPTED; the 409 proves
    /// the check is genuinely live rather than merely tolerant.</para></summary>
    [Fact]
    public async Task The_week_read_hands_back_a_cell_version_the_next_save_accepts__and_the_stale_one_409s()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();

        // A cell already has hours -- an earlier session, or a colleague. The client has NOT seen its version.
        Assert.Equal(HttpStatusCode.OK, (await SaveCellAsync(client, taskId, monday, 4m, null)).StatusCode);

        // A FRESH page load. The week read is the ONLY place the version can come from.
        var row = await ReadWeekRowAsync(client, monday, taskId);
        Assert.Equal(4m, row.Mon.Hours);
        var versionFromRead = row.Mon.RowVersion;
        Assert.NotNull(versionFromRead);   // <- exactly what was unreachable before M8.4.

        // The version from the READ is accepted by the WRITE, and advances by one.
        var edit = await SaveCellAsync(client, taskId, monday, 6m, versionFromRead);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var versionAfterWrite = (await edit.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
        Assert.Equal(versionFromRead!.Value + 1, versionAfterWrite);

        // Re-sending the now-stale version conflicts: the check is live, not decorative.
        var stale = await SaveCellAsync(client, taskId, monday, 8m, versionFromRead);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The winning write stands; the stale one changed nothing.
        Assert.Equal(6m, (await ReadWeekRowAsync(client, monday, taskId)).Mon.Hours);
    }

    /// <summary>The other half of the cell contract: an EMPTY cell carries null hours AND a null version.
    /// <c>null</c> is not "missing" here — it is precisely the <c>expectedVersion</c> that asserts "I believe
    /// this cell is empty", which is what makes the first write to an empty cell an INSERT instead of a 409.
    /// So the null the read hands out must itself round-trip as a working expectedVersion.</summary>
    [Fact]
    public async Task An_empty_cell_reads_back_a_null_version__and_that_null_is_what_creates_the_cell()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();

        await SaveCellAsync(client, taskId, monday, 4m, null);   // Monday filled; Tuesday left empty.

        var row = await ReadWeekRowAsync(client, monday, taskId);
        Assert.Null(row.Tue.Hours);
        Assert.Null(row.Tue.RowVersion);   // no row => nothing to version

        var created = await SaveCellAsync(client, taskId, monday.AddDays(1), 2m, row.Tue.RowVersion);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    /// <summary>The team view SUMS hours across users: a cell is backed by N rows carrying N different
    /// row_versions, so no single version exists to hand back. Synthesising one (max? first?) would give the
    /// client a token guarding a row it is not editing — worse than no token, because it would look like it
    /// works. Versions are therefore null BY CONSTRUCTION and the view is READ-ONLY. This pins that the API
    /// does not pretend otherwise: hours are real, the version is honestly absent.</summary>
    [Fact]
    public async Task The_team_view_returns_summed_hours_with_NULL_versions__and_does_not_pretend_otherwise()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var carolId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, carolId);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-TEAM");
        var taskId = await factory.SeedTaskAsync(backlogId, "Shared task");
        var monday = AMonday();

        var aliceClient = await factory.ClientAsync("alice");
        var carolClient = await factory.ClientAsync("carol");
        await SaveCellAsync(aliceClient, taskId, monday, 2m, null);
        await SaveCellAsync(carolClient, taskId, monday, 3m, null);

        // Each user's OWN view carries a real, usable version for their own row...
        var ownRow = await ReadWeekRowAsync(aliceClient, monday, taskId);
        Assert.Equal(2m, ownRow.Mon.Hours);
        Assert.NotNull(ownRow.Mon.RowVersion);

        // ...but the summed team view cannot, and says so.
        var teamRow = await ReadWeekRowAsync(aliceClient, monday, taskId, allUsers: true);
        Assert.Equal(5m, teamRow.Mon.Hours);
        Assert.Null(teamRow.Mon.RowVersion);
    }

    /// <summary>Asserts the WIRE, not merely the C# round-trip. The week route serialises the Core read-model
    /// straight onto the response (there is no week DTO), and M8.4 generates its TypeScript client from that
    /// JSON — so per <c>Dtos.cs</c>'s rule, "a field missing here is a field the client cannot send back".
    /// A day slot must therefore be an OBJECT <c>{hours, rowVersion}</c>, not the bare number it used to be.</summary>
    [Fact]
    public async Task The_week_read_JSON_exposes_a_per_cell_rowVersion_field()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 4m, null);

        var response = await client.GetAsync($"/api/timesheet/week?monday={monday:yyyy-MM-dd}");
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var task = doc.RootElement
            .EnumerateArray()
            .SelectMany(g => g.GetProperty("tasks").EnumerateArray())
            .Single(t => t.GetProperty("taskId").GetInt32() == taskId);

        var mon = task.GetProperty("mon");
        Assert.Equal(JsonValueKind.Object, mon.ValueKind);        // a CELL, not a bare number
        Assert.Equal(4m, mon.GetProperty("hours").GetDecimal());
        Assert.True(mon.GetProperty("rowVersion").GetInt64() > 0); // the field the TS client sends back

        var tue = task.GetProperty("tue");                         // empty cell: both halves null
        Assert.Equal(JsonValueKind.Null, tue.GetProperty("hours").ValueKind);
        Assert.Equal(JsonValueKind.Null, tue.GetProperty("rowVersion").ValueKind);
    }

    [Fact]
    public async Task A_cell_save_is_checked_and_a_second_save_reuses_the_returned_row_version()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var date = AMonday();

        var first = await SaveCellAsync(client, taskId, date, 1.5m, expectedVersion: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var v1 = (await first.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
        Assert.True(v1 > 0);

        // The version the checked call returned is the next expectedVersion -- never re-read it (rule #3).
        var second = await SaveCellAsync(client, taskId, date, 2m, expectedVersion: v1);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var v2 = (await second.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
        Assert.True(v2 > v1);
    }

    [Fact]
    public async Task A_cell_can_be_cleared_checked()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var date = AMonday();

        var saved = await SaveCellAsync(client, taskId, date, 1m, expectedVersion: null);
        var v1 = (await saved.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        var cleared = await ClearCellAsync(client, taskId, date, v1);

        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM TimeLogs;"));
    }

    /// <summary>OT-5. Someone else CHANGED the cell: the row still exists, so <c>deleted</c> is false, and
    /// <c>detail</c> is the only thing naming the cell (TimeLogs' <c>id</c> is 0). The competing write is
    /// arranged directly against the API's own database (<c>ApiFactory.OpenDb</c>), not via a second logical
    /// HTTP round-trip -- this is the same conflict <c>ConflictContractTests</c> proves through the probe,
    /// re-proven here through the REAL endpoint's own wiring.</summary>
    [Fact]
    public async Task A_stale_cell_save_against_a_changed_cell_is_409_with_deleted_false_and_detail_naming_the_cell()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var date = AMonday();

        var created = await SaveCellAsync(client, taskId, date, 1m, expectedVersion: null);
        var v1 = (await created.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE TimeLogs SET row_version = row_version + 1 WHERE task_id = @t AND work_date = @d;",
                new { t = taskId, d = Iso(date) });
        }

        var conflict = await SaveCellAsync(client, taskId, date, 3m, expectedVersion: v1);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("TimeLogs", body!.Table);
        Assert.Equal(0, body.Id); // natural key -> no id
        Assert.False(body.Deleted);
        Assert.Contains(taskId.ToString(), body.Detail);
    }

    /// <summary>The case only W2-B can prove empirically (see the class doc): deleting the cell leaves the
    /// TASK (and its team) intact, so rule #8's authorization pre-read still succeeds and the checked write
    /// reaches the repository's own conflict path, which distinguishes "changed" from "gone" via a fresh
    /// existence check.</summary>
    [Fact]
    public async Task A_stale_cell_save_against_a_deleted_cell_is_409_with_deleted_true()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var date = AMonday();

        var created = await SaveCellAsync(client, taskId, date, 1m, expectedVersion: null);
        var v1 = (await created.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "DELETE FROM TimeLogs WHERE task_id = @t AND work_date = @d;",
                new { t = taskId, d = Iso(date) });
        }

        var conflict = await SaveCellAsync(client, taskId, date, 3m, expectedVersion: v1);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("TimeLogs", body!.Table);
        Assert.True(body.Deleted);
        Assert.Contains(taskId.ToString(), body.Detail);
    }

    [Fact]
    public async Task Forty_hours_on_a_holiday_through_the_timesheet_endpoint_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var date = AMonday();
        await factory.SeedHolidayAsync(date);

        var response = await SaveCellAsync(client, taskId, date, 40m, expectedVersion: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.Contains("holiday", body!.Error, StringComparison.OrdinalIgnoreCase);

        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM TimeLogs;"));
    }

    /// <summary>Rule #8's headline danger for TimeLogs: <c>SaveCellCheckedAsync</c> takes <c>userId</c> as a
    /// PARAMETER, always <c>ctx.UserId</c>. <c>TimesheetSaveCellRequest</c> structurally has no
    /// <c>UserId</c> property at all -- this proves that even a raw payload smuggling one in anyway (what an
    /// out-of-date or hostile client would send) has no effect: System.Text.Json silently ignores the
    /// unmatched property, and the write always lands under the authenticated caller.</summary>
    [Fact]
    public async Task A_cell_save_ignores_a_userId_field_in_the_body_and_always_acts_as_the_authenticated_caller()
    {
        using var factory = new ApiFactory();
        var (client, aliceId, _, taskId) = await ArrangeAsync(factory, "alice");
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var date = AMonday();

        var payload = new { taskId, date, hours = 2m, expectedVersion = (long?)null, userId = bobId };
        var response = await client.PutAsJsonAsync("/api/timesheet/cell", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = factory.OpenDb();
        var storedUserId = await db.ExecuteScalarAsync<long>(
            "SELECT user_id FROM TimeLogs WHERE task_id = @t AND work_date = @d;",
            new { t = taskId, d = Iso(date) });
        Assert.Equal(aliceId, (int)storedUserId);
    }

    [Fact]
    public async Task A_cell_save_onto_another_teams_task_is_404_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-1");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Not yours");

        var response = await SaveCellAsync(client, otherTaskId, AMonday(), 2m, expectedVersion: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TimeLogs WHERE task_id = @t;", new { t = otherTaskId }));
    }

    [Fact]
    public async Task A_cell_clear_onto_another_teams_task_is_404()
    {
        using var factory = new ApiFactory();
        var (client, _, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-2");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Not yours either");

        var response = await ClearCellAsync(client, otherTaskId, AMonday(), expectedVersion: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ==== Smart Fill ===========================================================================================

    [Fact]
    public async Task Smart_fill_validates_and_applies_and_returns_the_written_cells_with_fresh_versions()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        var tuesday = monday.AddDays(1);
        var req = new SmartFillRequest(new[]
        {
            new SmartFillTaskRequest(taskId, new[]
            {
                new SmartFillCellRequest(monday, 2m),
                new SmartFillCellRequest(tuesday, 3m),
            }),
        });

        var validate = await client.PostAsJsonAsync("/api/smartfill/validate", req);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);

        var apply = await client.PostAsJsonAsync("/api/smartfill/apply", req);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        var cells = await apply.Content.ReadFromJsonAsync<List<TimeLogDto>>();
        var mondayCell = cells!.Single(c => c.TaskId == taskId && c.WorkDate == monday);
        var tuesdayCell = cells!.Single(c => c.TaskId == taskId && c.WorkDate == tuesday);
        Assert.Equal(2m, mondayCell.Hours);
        Assert.Equal(3m, tuesdayCell.Hours);
        Assert.True(mondayCell.RowVersion > 0);
        Assert.True(tuesdayCell.RowVersion > 0);
    }

    [Fact]
    public async Task Smart_fill_apply_rejecting_the_8h_cap_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var req = new SmartFillRequest(new[]
        {
            new SmartFillTaskRequest(taskId, new[] { new SmartFillCellRequest(AMonday(), 9m) }),
        });

        var response = await client.PostAsJsonAsync("/api/smartfill/apply", req);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM TimeLogs;"));
    }

    [Fact]
    public async Task Smart_fill_onto_another_teams_task_is_404_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-3");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Not yours");
        var req = new SmartFillRequest(new[]
        {
            new SmartFillTaskRequest(otherTaskId, new[] { new SmartFillCellRequest(AMonday(), 2m) }),
        });

        var response = await client.PostAsJsonAsync("/api/smartfill/apply", req);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TimeLogs WHERE task_id = @t;", new { t = otherTaskId }));
    }

    /// <summary>OT-14. <c>ApplySmartFillAsync</c> bumps every cell it writes but its own result carries no
    /// versions, and rule #7 excludes the filler from the SignalR echo -- so without the refreshed-grid
    /// response, the very next inline edit of a just-filled cell would 409 against the filler's OWN result,
    /// on the happy path, every time.</summary>
    [Fact]
    public async Task Running_smart_fill_then_immediately_editing_a_filled_cell_inline_is_200_not_409()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        var req = new SmartFillRequest(new[]
        {
            new SmartFillTaskRequest(taskId, new[] { new SmartFillCellRequest(monday, 2m) }),
        });

        var apply = await client.PostAsJsonAsync("/api/smartfill/apply", req);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        var cells = await apply.Content.ReadFromJsonAsync<List<TimeLogDto>>();
        var filledVersion = cells!.Single(c => c.TaskId == taskId && c.WorkDate == monday).RowVersion;

        var inlineEdit = await SaveCellAsync(client, taskId, monday, 4m, expectedVersion: filledVersion);

        Assert.Equal(HttpStatusCode.OK, inlineEdit.StatusCode);
    }

    // ==== Reports ==============================================================================================

    /// <summary>The trap a sibling agent found only via an unrelated test failing empty: minimal-API array
    /// binding cannot tell "the key is absent" from "the key is present with zero values", so a bound
    /// `int[]? teamIds` is NEVER null and an unfiltered request would silently return zero rows. This
    /// endpoint reads <c>HttpContext.Request.Query</c> directly instead -- this test is what actually
    /// proves it.</summary>
    [Fact]
    public async Task Reports_weekly_with_no_teamIds_query_param_returns_the_callers_own_rows_not_zero()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 2.5m, expectedVersion: null);

        var response = await client.GetAsync($"/api/reports/weekly?monday={monday:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<TimesheetWeeklyReportResponse>();
        Assert.NotEmpty(report!.DayTotals);
        Assert.Equal(2.5m, report.DayTotals.Single().TotalHours);
        Assert.Equal(1, report.DaysLogged.Logged);
    }

    [Fact]
    public async Task Reports_monthly_aggregates_the_callers_hours_for_the_month()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 4m, expectedVersion: null);

        var response = await client.GetAsync($"/api/reports/monthly?year={monday.Year}&month={monday.Month}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<TimesheetMonthlyReportResponse>();
        Assert.Contains(report!.MonthlyTotals, t => t.TotalHours == 4m);
        Assert.NotEmpty(report.ProjectTree);
    }

    /// <summary>R6 (rule #5): a team id the caller is not a member of, put on the query string of EITHER
    /// route, must never leak that team's rows.</summary>
    [Fact]
    public async Task Reports_and_export_never_leak_a_team_the_caller_is_not_a_member_of()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory, "alice");
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 2m, expectedVersion: null);

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-4");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Secret");
        var bobClient = await factory.ClientAsync("bob");
        await SaveCellAsync(bobClient, otherTaskId, monday, 5m, expectedVersion: null);

        var reportResponse = await client.GetAsync(
            $"/api/reports/weekly?monday={monday:yyyy-MM-dd}&teamIds={otherTeamId}");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var report = await reportResponse.Content.ReadFromJsonAsync<TimesheetWeeklyReportResponse>();
        Assert.Empty(report!.DayTotals);

        var exportResponse = await client.GetAsync(
            $"/api/export/markdown?year={monday.Year}&month={monday.Month}&teamIds={otherTeamId}");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var markdown = await exportResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-4", markdown);
        Assert.DoesNotContain("bob", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_logs_banner_route_exists_and_returns_a_typed_list()
    {
        using var factory = new ApiFactory();
        var (client, _, _, _) = await ArrangeAsync(factory);

        var response = await client.GetAsync("/api/reports/missing-logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<List<MissingLogWarning>>());
    }

    // ==== Export ===============================================================================================

    [Fact]
    public async Task Export_excel_returns_a_non_empty_xlsx_file()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory);
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 1m, expectedVersion: null);

        var response = await client.GetAsync($"/api/export/excel?year={monday.Year}&month={monday.Month}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
    }

    /// <summary>R6 / rule #5's OTHER half: absence of <c>teamIds</c> must default to the caller's own
    /// membership, never leak the whole company. <c>ExportFilter.TeamIds</c> is a trailing optional
    /// defaulting to null, and the 4-arg ctor every WPF call site uses would export everyone.</summary>
    [Fact]
    public async Task Export_with_no_teamIds_query_param_returns_the_callers_own_rows_not_the_whole_company()
    {
        using var factory = new ApiFactory();
        var (client, _, _, taskId) = await ArrangeAsync(factory, "alice");
        var monday = AMonday();
        await SaveCellAsync(client, taskId, monday, 1m, expectedVersion: null);

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-5");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Not yours");
        var bobClient = await factory.ClientAsync("bob");
        await SaveCellAsync(bobClient, otherTaskId, monday, 9m, expectedVersion: null);

        var response = await client.GetAsync($"/api/export/markdown?year={monday.Year}&month={monday.Month}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.Contains("alice", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-5", markdown);
    }

    // ==== helpers ==============================================================================================

    private static async Task<(HttpClient Client, int UserId, int TeamId, int TaskId)> ArrangeAsync(
        ApiFactory factory, string userName = "alice")
    {
        var userId = await factory.SeedUserAsync(userName, ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync($"Team-{userName}", userId);
        var backlogId = await factory.SeedBacklogAsync(teamId, $"REQ-{userName}");
        var taskId = await factory.SeedTaskAsync(backlogId, "Implement");
        var client = await factory.ClientAsync(userName);
        return (client, userId, teamId, taskId);
    }

    private static Task<HttpResponseMessage> SaveCellAsync(
        HttpClient client, int taskId, DateOnly date, decimal hours, long? expectedVersion) =>
        client.PutAsJsonAsync("/api/timesheet/cell", new TimesheetSaveCellRequest(taskId, date, hours, expectedVersion));

    /// <summary>Reads the week the way the real client does — over HTTP, through the one timesheet read route
    /// there is — and returns the row for one task. Everything the client can possibly know about a cell's
    /// version comes from here.</summary>
    private static async Task<WeekRow> ReadWeekRowAsync(
        HttpClient client, DateOnly monday, int taskId, bool allUsers = false)
    {
        var url = $"/api/timesheet/week?monday={monday:yyyy-MM-dd}" + (allUsers ? "&allUsers=true" : "");
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var groups = await response.Content.ReadFromJsonAsync<List<WeekBacklogGroup>>();
        return groups!.SelectMany(g => g.Tasks).Single(t => t.TaskId == taskId);
    }

    private static async Task<HttpResponseMessage> ClearCellAsync(
        HttpClient client, int taskId, DateOnly date, long expectedVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/timesheet/cell")
        {
            Content = JsonContent.Create(new TimesheetClearCellRequest(taskId, date, expectedVersion)),
        };
        return await client.SendAsync(request);
    }

    /// <summary>A known Monday that is not a holiday by default, computed rather than hard-coded so the
    /// fixture does not silently start testing the weekend/holiday rule instead of the rule under test.</summary>
    private static DateOnly AMonday()
    {
        var date = new DateOnly(2026, 7, 6);
        while (date.DayOfWeek != DayOfWeek.Monday) date = date.AddDays(1);
        return date;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
