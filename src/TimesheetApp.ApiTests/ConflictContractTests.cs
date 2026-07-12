using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>The single highest-value contract in the milestone: the RAW JSON of both error channels.
///
/// <para>Four Wave-2 agents write against these bodies. If the shape is not pinned by a test, it is not
/// pinned — and "no test proves the wire format" is a choice, not a fact. Every assertion below reads the
/// literal property names off the response, not a deserialized C# record, because the property names ARE the
/// contract M8.4 generates its client from.</para></summary>
public sealed class ConflictContractTests
{
    // ---- 409: the version-conflict channel (ConcurrencyConflictException -> ExceptionMapper) -----------

    /// <summary>OT-5. Someone else CHANGED the cell: the row still exists, so <c>deleted</c> is false.
    ///
    /// <para><c>TimeLogs</c> is keyed by the natural (user_id, task_id, work_date) triple, so <c>id</c> is
    /// <b>0</b> and <c>detail</c> is the ONLY thing that can name the cell. Without it the client is told
    /// merely "something in TimeLogs changed" — useless to a user with a week's grid in front of them.</para></summary>
    [Fact]
    public async Task A_stale_version_on_a_changed_cell_is_409_with_deleted_false_and_detail_naming_the_cell()
    {
        using var factory = new ApiFactory();
        var (client, taskId, date) = await ArrangeAsync(factory);

        // v1: the cell is created (expectedVersion null == "I believe this cell is empty").
        var created = await SaveCellAsync(client, taskId, date, 1m, expectedVersion: null);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var v1 = (await created.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        // Another client (a second tab) writes: the cell moves to v2.
        var moved = await SaveCellAsync(client, taskId, date, 2m, expectedVersion: v1);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        // The first client still believes it is at v1.
        var conflict = await SaveCellAsync(client, taskId, date, 3m, expectedVersion: v1);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await RawAsync(conflict);

        Assert.Equal("TimeLogs", body.GetProperty("table").GetString());
        Assert.Equal(0, body.GetProperty("id").GetInt64());          // natural key -> no id
        Assert.False(body.GetProperty("deleted").GetBoolean());      // changed, NOT deleted
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    /// <summary>OT-5, the other half. Someone else DELETED the cell: <c>deleted</c> must be true, because
    /// the user needs different wording for "was changed" and "is gone". <c>rowsAffected == 0</c> conflates
    /// them, which is why the repositories run an existence check on the conflict path.</summary>
    [Fact]
    public async Task A_stale_version_on_a_deleted_cell_is_409_with_deleted_true()
    {
        using var factory = new ApiFactory();
        var (client, taskId, date) = await ArrangeAsync(factory);

        var created = await SaveCellAsync(client, taskId, date, 1m, expectedVersion: null);
        var v1 = (await created.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        // Another client clears the cell.
        using var clear = new HttpRequestMessage(HttpMethod.Delete, "/probe/cell")
        {
            Content = JsonContent.Create(new ProbeClearRequest(taskId, date, v1)),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(clear)).StatusCode);

        // The first client writes at a version that no longer exists anywhere.
        var conflict = await SaveCellAsync(client, taskId, date, 3m, expectedVersion: v1);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await RawAsync(conflict);

        Assert.Equal("TimeLogs", body.GetProperty("table").GetString());
        Assert.True(body.GetProperty("deleted").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    // ---- 400: the business-rule channel (SaveResult.Ok == false — NEVER an exception) ------------------

    /// <summary>OT-6. The rule fires through the API, and NOTHING is written.
    ///
    /// <para>This channel never throws, so <c>ExceptionMapper</c> never sees it. An endpoint that ignores
    /// <c>SaveCellResult.Ok</c> returns 200 on a rejected write and the user watches their hours vanish.</para></summary>
    [Fact]
    public async Task Forty_hours_on_a_holiday_is_400_with_the_validation_body_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, taskId, date) = await ArrangeAsync(factory);
        await factory.SeedHolidayAsync(date);

        var response = await SaveCellAsync(client, taskId, date, 40m, expectedVersion: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await RawAsync(response);
        var error = body.GetProperty("error").GetString();
        Assert.Contains("holiday", error!, StringComparison.OrdinalIgnoreCase);

        await AssertNoTimeLogsAsync(factory);
    }

    /// <summary>The 8h/day cap (XC-03) — a different rule reaching the same channel, so the 400 is the
    /// contract and not an accident of the holiday check happening to run first.</summary>
    [Fact]
    public async Task Forty_hours_on_an_ordinary_working_day_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, taskId, date) = await ArrangeAsync(factory);

        var response = await SaveCellAsync(client, taskId, date, 40m, expectedVersion: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await RawAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));

        await AssertNoTimeLogsAsync(factory);
    }

    [Fact]
    public async Task A_weekend_cell_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, taskId, weekday) = await ArrangeAsync(factory);

        var saturday = weekday;
        while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);

        var response = await SaveCellAsync(client, taskId, saturday, 1m, expectedVersion: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoTimeLogsAsync(factory);
    }

    // ---- 400: the THIRD channel, not named in the design (ArgumentException from IStandupService) -------

    /// <summary><c>IStandupService</c> signals bad input by THROWING <c>ArgumentException</c> — nine sites,
    /// including <c>UpdateIssueCheckedAsync</c>. Unmapped, an invalid standup status comes back as a 500.
    /// <c>ExceptionMapper</c> maps it to the same 400 + <c>ValidationBody</c> as the SaveResult channel.</summary>
    [Fact]
    public async Task An_invalid_standup_issue_status_is_400_not_500()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/probe/issue", new ProbeIssueRequest(EntryId: 1, IssueText: "something broke", Status: "bogus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await RawAsync(response);
        Assert.Contains("bogus", body.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_standup_issue_text_is_400_not_500()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/probe/issue", new ProbeIssueRequest(EntryId: 1, IssueText: "   ", Status: "open"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await RawAsync(response)).GetProperty("error").GetString()));
    }

    // ---- The happy path, so the tests above are known to be rejecting rather than merely failing --------

    [Fact]
    public async Task A_valid_cell_save_is_200_and_hands_back_the_new_row_version()
    {
        using var factory = new ApiFactory();
        var (client, taskId, date) = await ArrangeAsync(factory);

        var first = await SaveCellAsync(client, taskId, date, 1.5m, expectedVersion: null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var v1 = (await first.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
        Assert.True(v1 > 0);

        // The version the checked call returned is the next expectedVersion — never re-read it.
        var second = await SaveCellAsync(client, taskId, date, 2m, expectedVersion: v1);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var v2 = (await second.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
        Assert.True(v2 > v1);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static async Task<(HttpClient Client, int TaskId, DateOnly Date)> ArrangeAsync(ApiFactory factory)
    {
        var userId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", userId);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-001");
        var taskId = await factory.SeedTaskAsync(backlogId, "Implement");

        var client = await factory.ClientAsync("alice");
        return (client, taskId, AWeekday());
    }

    private static Task<HttpResponseMessage> SaveCellAsync(
        HttpClient client, int taskId, DateOnly date, decimal hours, long? expectedVersion) =>
        client.PutAsJsonAsync("/probe/cell", new ProbeCellRequest(taskId, date, hours, expectedVersion));

    private static async Task<JsonElement> RawAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertNoTimeLogsAsync(ApiFactory factory)
    {
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM TimeLogs;"));
    }

    /// <summary>A weekday that is not a holiday. Computed rather than hard-coded so the fixture does not
    /// silently start testing the weekend rule instead of the rule under test.</summary>
    private static DateOnly AWeekday()
    {
        var date = new DateOnly(2026, 7, 6);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        return date;
    }
}
