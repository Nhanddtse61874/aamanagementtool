using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>M9 P3b/P3f. Covers <c>GET /api/tasklist</c> and <c>GET /api/tasklist/export</c> — the Task List
/// screen's read model and its monthly markdown.
///
/// <para><b>The empty-list trap.</b> <c>ITaskListService.GetRowsAsync</c> takes a NON-NULLABLE
/// <c>teamIds</c>, and per <c>IBacklogRepository.SearchAsync</c>'s documented R6 rule an EMPTY list means NO
/// TEAMS — it yields nothing. So "don't filter" cannot be expressed as <c>[]</c>, and it must not be
/// expressed as <c>null</c> either (null there means EVERY team). The endpoint therefore resolves the scope
/// through <c>EffectiveTeamIds</c>, which defaults an absent <c>teamIds</c> key to the caller's own
/// memberships. <see cref="The_task_list_with_no_teamIds_query_param_returns_the_callers_own_rows_not_zero"/>
/// is what proves the default is the caller's teams and not the empty set.</para>
///
/// <para><b>The archive trap.</b> <c>ITaskListArchiveService</c> has TWO month methods and only one of them
/// belongs on a web route: <c>ExportMonthAsync</c> writes a file to the SERVER's disk and hands back a server
/// path (useless to a browser) — and it builds that file with <c>teamIds: null</c>, i.e. EVERY team.
/// <c>BuildMonthMarkdownAsync</c> is the content-only, team-scoped one, and it is what
/// <c>GET /api/tasklist/export</c> calls. <see cref="The_task_list_export_never_leaks_a_team_the_caller_is_not_a_member_of"/>
/// pins that the scope is real.</para></summary>
public sealed class TaskListEndpointsTests
{
    // ==== GET /api/tasklist =================================================================================

    [Fact]
    public async Task The_task_list_returns_a_row_and_a_gantt_bar_for_a_backlog_in_the_selected_month()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-1");
        await SetPeriodAsync(factory, backlogId, "2026-07");
        await SetScheduleAsync(factory, backlogId, new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 17));

        var response = await client.GetAsync("/api/tasklist?year=2026&month=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<TaskListScreenDto>();
        var row = Assert.Single(screen!.Rows);
        Assert.Equal(backlogId, row.BacklogId);
        Assert.Equal("REQ-1", row.BacklogCode);

        // The grid and the chart come from ONE snapshot -- the bar must describe the same backlog.
        var bar = Assert.Single(screen.Gantt.Bars);
        Assert.Equal(backlogId, bar.BacklogId);
        Assert.NotEmpty(screen.Gantt.Axis);
    }

    /// <summary>The complement of the test above, and NOT a defect: <c>GanttBuilder</c> derives the chart's
    /// axis from the backlogs' own dates, so when nothing in scope has a <c>start_date</c>, a
    /// <c>deadline_internal</c> or an <c>end_date</c> there is no axis to draw and the model is empty. A
    /// backlog with only a period is a perfectly good GRID row that simply has no BAR. Pinned so that a future
    /// change which starts synthesising a bar out of nothing has to argue with a test.</summary>
    [Fact]
    public async Task A_backlog_with_no_dates_is_a_grid_row_with_no_gantt_bar()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-NODATES");
        await SetPeriodAsync(factory, backlogId, "2026-07");

        var response = await client.GetAsync("/api/tasklist?year=2026&month=7");

        var screen = await response.Content.ReadFromJsonAsync<TaskListScreenDto>();
        Assert.Single(screen!.Rows);
        Assert.Empty(screen.Gantt.Bars);
        Assert.Empty(screen.Gantt.Axis);
    }

    /// <summary>The trap P1 flagged and this endpoint's whole reason for calling <c>EffectiveTeamIds</c>:
    /// <c>GetRowsAsync</c>'s <c>teamIds</c> is non-nullable and an EMPTY list means NO TEAMS, so an endpoint
    /// that passed <c>[]</c> to mean "don't filter" would answer every unfiltered request with zero rows —
    /// the Task List screen would simply be blank, for everyone, with nothing to indicate why.</summary>
    [Fact]
    public async Task The_task_list_with_no_teamIds_query_param_returns_the_callers_own_rows_not_zero()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-MINE");
        await SetPeriodAsync(factory, backlogId, "2026-07");

        var response = await client.GetAsync("/api/tasklist?year=2026&month=7");   // <- no teamIds at all

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<TaskListScreenDto>();
        Assert.NotEmpty(screen!.Rows);
    }

    /// <summary>R6 / rule #5, the other half: a team id the caller is not a member of, put on the query
    /// string, must never return that team's backlogs. The intersection with <c>ctx.MemberTeamIds</c> leaves
    /// the empty set, and the empty set correctly yields nothing.</summary>
    [Fact]
    public async Task The_task_list_never_leaks_a_team_the_caller_is_not_a_member_of()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var bobTeamId = await factory.SeedTeamAsync("Team B", bobId);
        var secretId = await factory.SeedBacklogAsync(bobTeamId, "SECRET-TL");
        await SetPeriodAsync(factory, secretId, "2026-07");

        var response = await client.GetAsync($"/api/tasklist?year=2026&month=7&teamIds={bobTeamId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var screen = await response.Content.ReadFromJsonAsync<TaskListScreenDto>();
        Assert.Empty(screen!.Rows);
        Assert.Empty(screen.Gantt.Bars);
    }

    /// <summary>P1's "All months" sentinel: <c>month == 0</c> lists every backlog regardless of period —
    /// INCLUDING one whose <c>period_month</c> is NULL, which no concrete month can ever match. The seed
    /// leaves <c>period_month</c> null, so this is exactly the case that distinguishes the two.</summary>
    [Fact]
    public async Task Month_zero_means_all_months_and_includes_a_backlog_with_no_period()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        await factory.SeedBacklogAsync(teamId, "REQ-NOPERIOD");   // period_month stays NULL

        var concrete = await client.GetAsync("/api/tasklist?year=2026&month=7");
        var allMonths = await client.GetAsync("/api/tasklist?year=2026&month=0");

        Assert.Empty((await concrete.Content.ReadFromJsonAsync<TaskListScreenDto>())!.Rows);
        Assert.NotEmpty((await allMonths.Content.ReadFromJsonAsync<TaskListScreenDto>())!.Rows);
    }

    /// <summary>DATA-03: DEFAULT is the hidden per-team catch-all that absorbs unassigned time. It is not a
    /// work item and must never surface as a Task List row — over the wire, not just in Core.</summary>
    [Fact]
    public async Task The_task_list_never_shows_the_DEFAULT_backlog()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        await factory.SeedBacklogAsync(teamId, "DEFAULT");

        var response = await client.GetAsync("/api/tasklist?year=2026&month=0");

        var screen = await response.Content.ReadFromJsonAsync<TaskListScreenDto>();
        Assert.DoesNotContain(screen!.Rows, r => r.BacklogCode == "DEFAULT");
    }

    /// <summary>The row is a DTO, not the Core read-model. <c>TaskListRow.Tags</c> is a list of <c>Tag</c>
    /// ENTITIES (which carry a <c>createdAt</c> the wire has no use for) and <c>.Tasks</c> a list of
    /// <c>TaskItem</c> entities — serialising those straight out would put entity shapes on the wire and into
    /// the generated client. This asserts the projection actually happened: tags arrive as <c>TagDto</c>
    /// (no <c>createdAt</c>) and tasks as <c>TaskItemDto</c> (with the <c>rowVersion</c> the inline status
    /// editor sends back).</summary>
    [Fact]
    public async Task A_task_list_row_projects_tags_and_tasks_into_DTOs_not_core_entities()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-SHAPE");
        await SetPeriodAsync(factory, backlogId, "2026-07");
        var taskId = await factory.SeedTaskAsync(backlogId, "Implement");
        await SeedTagOnBacklogAsync(factory, backlogId, "urgent");

        var response = await client.GetAsync("/api/tasklist?year=2026&month=7");
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("rows").EnumerateArray().Single();

        var tag = row.GetProperty("tags").EnumerateArray().Single();
        Assert.Equal("urgent", tag.GetProperty("text").GetString());
        Assert.False(tag.TryGetProperty("createdAt", out _));      // TagDto drops it; the Tag entity has it.
        Assert.True(tag.GetProperty("rowVersion").GetInt64() > 0);

        var task = row.GetProperty("tasks").EnumerateArray().Single();
        Assert.Equal(taskId, task.GetProperty("id").GetInt32());
        Assert.Equal("Implement", task.GetProperty("taskName").GetString());
        Assert.True(task.GetProperty("rowVersion").GetInt64() > 0);
    }

    // ==== GET /api/tasklist/export ===========================================================================

    [Fact]
    public async Task The_task_list_export_returns_markdown_for_the_month()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-EXPORT");
        await SetPeriodAsync(factory, backlogId, "2026-07");

        var response = await client.GetAsync("/api/tasklist/export?year=2026&month=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.Contains("REQ-EXPORT", markdown);
    }

    /// <summary>R6 on the ARCHIVE path, which is the one the brief for this task got wrong:
    /// <c>BuildMonthMarkdownAsync</c>'s first parameter is <c>IReadOnlyList&lt;int&gt;? teamIds</c>, and
    /// <c>null</c> there means EVERY TEAM (it flows into <c>SearchAsync(null, teamIds)</c>). Calling it the
    /// obvious way — <c>BuildMonthMarkdownAsync(null, year, month)</c>, exactly as <c>ExportMonthAsync</c>
    /// does internally — would hand any authenticated user a markdown dump of the whole company's backlogs.
    /// The route passes <c>EffectiveTeamIds</c> instead. This is what proves it.</summary>
    [Fact]
    public async Task The_task_list_export_never_leaks_a_team_the_caller_is_not_a_member_of()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory, "alice");
        var mineId = await factory.SeedBacklogAsync(teamId, "REQ-MINE");
        await SetPeriodAsync(factory, mineId, "2026-07");

        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var bobTeamId = await factory.SeedTeamAsync("Team B", bobId);
        var secretId = await factory.SeedBacklogAsync(bobTeamId, "SECRET-EXPORT");
        await SetPeriodAsync(factory, secretId, "2026-07");

        var response = await client.GetAsync("/api/tasklist/export?year=2026&month=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.Contains("REQ-MINE", markdown);
        Assert.DoesNotContain("SECRET-EXPORT", markdown);
    }

    /// <summary>No data for the month => <c>BuildMonthMarkdownAsync</c> returns null (its "no data => no
    /// file" rule). There is no empty file to hand back, so the route says so rather than serving a
    /// zero-byte download the user cannot tell apart from a broken one.</summary>
    [Fact]
    public async Task The_task_list_export_for_a_month_with_no_data_is_400()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory);

        var response = await client.GetAsync("/api/tasklist/export?year=2026&month=7");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.Contains("no", body!.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ==== helpers ============================================================================================

    private static async Task<(HttpClient Client, int UserId, int TeamId)> ArrangeAsync(
        ApiFactory factory, string userName = "alice")
    {
        var userId = await factory.SeedUserAsync(userName, ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync($"Team-{userName}", userId);
        var client = await factory.ClientAsync(userName);
        return (client, userId, teamId);
    }

    /// <summary><c>ApiFactory.SeedBacklogAsync</c> leaves <c>period_month</c> NULL, which no concrete month
    /// matches — so a test about a specific month has to set it.</summary>
    private static async Task SetPeriodAsync(ApiFactory factory, int backlogId, string periodMonth)
    {
        using var db = factory.OpenDb();
        await db.ExecuteAsync(
            "UPDATE Backlogs SET period_month = @p WHERE id = @id;",
            new { p = periodMonth, id = backlogId });
    }

    /// <summary>The seed leaves every DATE column null too, and <c>GanttBuilder</c> draws its axis FROM those
    /// dates — no dates anywhere in scope means no axis and therefore no bars. A test about the CHART has to
    /// give the backlog a schedule; see <see cref="A_backlog_with_no_dates_is_a_grid_row_with_no_gantt_bar"/>
    /// for the other half.</summary>
    private static async Task SetScheduleAsync(
        ApiFactory factory, int backlogId, DateOnly start, DateOnly deadlineInternal)
    {
        using var db = factory.OpenDb();
        await db.ExecuteAsync(
            "UPDATE Backlogs SET start_date = @s, deadline_internal = @d WHERE id = @id;",
            new
            {
                s = start.ToString("yyyy-MM-dd"),
                d = deadlineInternal.ToString("yyyy-MM-dd"),
                id = backlogId,
            });
    }

    private static async Task SeedTagOnBacklogAsync(ApiFactory factory, int backlogId, string text)
    {
        using var db = factory.OpenDb();
        var tagId = await db.ExecuteScalarAsync<int>(
            @"INSERT INTO Tags(text, icon, color, created_at, row_version)
              VALUES(@text, 'star', '#FF0000', @now, 1);
              SELECT last_insert_rowid();",
            new { text, now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });

        await db.ExecuteAsync(
            "INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@b, @t);",
            new { b = backlogId, t = tagId });
    }
}
