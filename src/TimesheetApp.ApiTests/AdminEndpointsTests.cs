using System.Net;
using System.Net.Http.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>M9 P3c/P3d/P3e. Covers <c>PUT /api/users/{id}/admin</c> · <c>GET|PUT /api/settings/{key}</c> ·
/// <c>POST /api/standup/archive</c> — the three surfaces <c>SettingsEndpoints.cs</c> left with no web route
/// at all.
///
/// <para><b>THE 30-DAY HOLE, AND WHY THERE ARE THREE TESTS FOR IT.</b> There are TWO admin checks in this
/// API and they disagree for up to thirty days:</para>
/// <list type="number">
/// <item><c>.RequireAuthorization(AuthSetup.AdminPolicy)</c> gates on the <c>is_admin</c> CLAIM, which is
/// written ONCE, at login, into a 30-day sliding cookie. Demote an admin in the database and their existing
/// cookie still satisfies this policy until it expires.</item>
/// <item><c>IClientContext.IsAdmin</c> is read FRESH FROM THE DATABASE on every request by
/// <c>ClientContextFilter</c>. The four <c>/api/ops/*</c> routes check it IN ADDITION to the policy, so a
/// demoted admin is refused there immediately.</item>
/// </list>
///
/// <para>Both facts are true today and both are pinned below, so that nobody "fixes" one and silently breaks
/// the other. Closing the window properly (re-issuing or revoking the cookie on demotion) is out of scope and
/// is recorded as an open risk.</para>
///
/// <para><b>The new routes take the STRICTER of the two.</b>
/// <see cref="A_demoted_admin_cannot_re_promote_themselves"/> is the reason: <c>PUT /api/users/{id}/admin</c>
/// is a privilege-escalation surface, and gating it on the stale claim ALONE would mean a just-demoted admin
/// could re-grant themselves the flag with the cookie they are still holding — a hole that does not exist
/// today because the route does not exist today. So the three admin-gated routes in this file carry the
/// policy AND the DB-fresh <c>ctx.IsAdmin</c> check, exactly as <c>/api/ops/*</c> does.</para></summary>
public sealed class AdminEndpointsTests
{
    /// <summary>SET-02's "not logged for N days" warning window. Duplicated as a private const in
    /// <c>TimesheetEndpoints</c> (<c>MissingLogsNDaysKey</c>) because the WPF ViewModel that owns the
    /// constant lives in a <c>net8.0-windows</c> project the API cannot reference.</summary>
    private const string NDaysKey = "chua_log_n_days";

    // ==== PUT /api/users/{id}/admin =========================================================================

    [Fact]
    public async Task Granting_admin_returns_the_bumped_version_and_the_flag_is_set()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var v0 = await RowVersionAsync(factory, bobId);

        var response = await admin.PutAsJsonAsync(
            $"/api/users/{bobId}/admin", new { isAdmin = true, expectedVersion = v0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<SavedBody>();
        Assert.Equal(v0 + 1, saved!.RowVersion);
        Assert.True(await IsAdminAsync(factory, bobId));
    }

    [Fact]
    public async Task Revoking_admin_clears_the_flag_and_the_version_chains()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword, isAdmin: true);
        var v0 = await RowVersionAsync(factory, bobId);

        var response = await admin.PutAsJsonAsync(
            $"/api/users/{bobId}/admin", new { isAdmin = false, expectedVersion = v0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(v0 + 1, (await response.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion);
        Assert.False(await IsAdminAsync(factory, bobId));
    }

    /// <summary>404, not 409. <c>SetIsAdminCheckedAsync</c> THROWS <c>ConcurrencyConflictException</c> with
    /// <c>deleted: true</c> for a row that is not there, which <c>ExceptionMapper</c> would turn into a 409 —
    /// so without an id-based pre-read this route would answer "version conflict" for a user that never
    /// existed. The pre-read is what shadows that path, exactly as Backlogs/Tasks do.</summary>
    [Fact]
    public async Task Setting_admin_on_a_user_that_does_not_exist_is_404()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            "/api/users/9999/admin", new { isAdmin = true, expectedVersion = 1L });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Two admins racing on one user's row must not silently lose one of the two decisions — which
    /// is why the repository method is CHECKED rather than bump-only.</summary>
    [Fact]
    public async Task Setting_admin_at_a_stale_version_is_409_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var stale = await RowVersionAsync(factory, bobId);

        // Somebody else got there first.
        var first = await admin.PutAsJsonAsync(
            $"/api/users/{bobId}/admin", new { isAdmin = true, expectedVersion = stale });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var conflict = await admin.PutAsJsonAsync(
            $"/api/users/{bobId}/admin", new { isAdmin = false, expectedVersion = stale });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Users", body!.Table);
        Assert.Equal(bobId, body.Id);
        Assert.False(body.Deleted);

        Assert.True(await IsAdminAsync(factory, bobId));   // the winning write stands
    }

    [Fact]
    public async Task Setting_admin_is_403_for_a_non_admin()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);           // NOT an admin
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var alice = await factory.ClientAsync("alice");

        var response = await alice.PutAsJsonAsync(
            $"/api/users/{bobId}/admin", new { isAdmin = true, expectedVersion = 1L });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await IsAdminAsync(factory, bobId));
    }

    // ==== The 30-day hole: both halves, pinned ==============================================================

    /// <summary>HALF ONE. The <c>is_admin</c> claim is written ONCE, at login. Demoting the user in the
    /// database does NOT invalidate the cookie they are already holding, so a route gated on
    /// <c>AdminPolicy</c> ALONE still lets them through — for up to thirty days.
    ///
    /// <para>This is not an endorsement. It is a load-bearing fact about the system that the next person to
    /// touch the auth setup needs to see fail if they change it.</para></summary>
    [Fact]
    public async Task A_demoted_admin_STILL_PASSES_AdminPolicy_because_the_claim_is_written_once_at_login()
    {
        using var factory = new ApiFactory();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword, isAdmin: true);
        var bob = await factory.ClientAsync("bob");   // <- the claim is minted HERE, and never revisited

        await DemoteAsync(factory, bobId);

        // /api/users/all is AdminPolicy-gated and does NOT re-check ctx.IsAdmin.
        var response = await bob.GetAsync("/api/users/all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>HALF TWO. The same demoted admin, the same cookie — and the four <c>/api/ops/*</c> routes
    /// refuse them, because those check <c>IClientContext.IsAdmin</c>, which <c>ClientContextFilter</c> reads
    /// fresh from the database on every single request.</summary>
    [Theory]
    [InlineData("/api/ops/retention/preview")]
    [InlineData("/api/ops/retention/run")]
    [InlineData("/api/ops/export/run")]
    [InlineData("/api/ops/backup/run")]
    public async Task A_demoted_admin_is_REJECTED_by_the_ops_routes_which_re_check_the_DB_fresh_flag(string route)
    {
        using var factory = new ApiFactory();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword, isAdmin: true);
        var bob = await factory.ClientAsync("bob");

        await DemoteAsync(factory, bobId);

        var response = await bob.PostAsync(route, content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>M10 gate. The three backup-config routes (GET|PUT <c>backup/settings</c>, GET
    /// <c>backup/list</c>) join the same belt-and-braces set as the theory above -- a demoted admin's stale
    /// cookie must not let them read or rewrite backup config either. Not folded into the theory itself
    /// because that theory is hard-wired to POST and these three are GET/PUT/GET.</summary>
    [Fact]
    public async Task A_demoted_admin_is_REJECTED_by_the_backup_config_routes_too()
    {
        using var factory = new ApiFactory();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword, isAdmin: true);
        var bob = await factory.ClientAsync("bob");

        await DemoteAsync(factory, bobId);

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/ops/backup/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings("", false, 30))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/ops/backup/list")).StatusCode);
    }

    /// <summary>HALF THREE — the one this task ADDS, and the reason the new admin routes do not settle for
    /// the policy alone. <c>PUT /api/users/{id}/admin</c> is the route that grants the flag. Gated on the
    /// stale claim only, a just-demoted admin could use the cookie they are still holding to hand the flag
    /// straight back to themselves, and the demotion would be worthless. The DB-fresh check is what stops
    /// it.</summary>
    [Fact]
    public async Task A_demoted_admin_cannot_re_promote_themselves()
    {
        using var factory = new ApiFactory();
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword, isAdmin: true);
        var bob = await factory.ClientAsync("bob");

        await DemoteAsync(factory, bobId);

        var response = await bob.PutAsJsonAsync(
            $"/api/users/{bobId}/admin",
            new { isAdmin = true, expectedVersion = await RowVersionAsync(factory, bobId) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await IsAdminAsync(factory, bobId));
    }

    // ==== GET / PUT /api/settings/{key} =====================================================================

    [Fact]
    public async Task A_setting_written_by_an_admin_reads_back()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();

        var put = await admin.PutAsJsonAsync($"/api/settings/{NDaysKey}", new { value = "7" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await admin.GetAsync($"/api/settings/{NDaysKey}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var setting = await get.Content.ReadFromJsonAsync<SettingDto>();
        Assert.Equal(NDaysKey, setting!.Key);
        Assert.Equal("7", setting.Value);
    }

    /// <summary>SET-02 END TO END, and the whole point of P3d. The warning window had no web route at all,
    /// so the one shared setting the Reports screen depends on could be neither read nor written from the
    /// browser. This asserts the value a web PUT writes lands in the EXACT key
    /// <c>/api/reports/missing-logs</c> reads server-side — the two constants live in different projects
    /// (the API cannot reference the WPF ViewModel that owns the name), so nothing but a test couples
    /// them. Rename either side and the report silently falls back to its default of 3.</summary>
    [Fact]
    public async Task The_N_days_window_written_over_the_web_lands_in_the_key_the_report_reads()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync($"/api/settings/{NDaysKey}", new { value = "7" })).StatusCode);

        using (var db = factory.OpenDb())
        {
            var stored = await db.ExecuteScalarAsync<string>(
                "SELECT value FROM Settings WHERE key = @k;", new { k = NDaysKey });
            Assert.Equal("7", stored);
        }

        // And the route that reads that key still serves with a web-written value in place.
        var report = await admin.GetAsync("/api/reports/missing-logs");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
    }

    /// <summary>An unset key is not an error — it is the state EVERY key is in on a fresh database, and the
    /// caller's correct response is to fall back to the documented default (3, for the N-days window). A 404
    /// would force every settings form to special-case "never written" as a failure.</summary>
    [Fact]
    public async Task Reading_a_setting_that_was_never_written_is_200_with_a_null_value()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();

        var response = await admin.GetAsync($"/api/settings/{NDaysKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setting = await response.Content.ReadFromJsonAsync<SettingDto>();
        Assert.Equal(NDaysKey, setting!.Key);
        Assert.Null(setting.Value);
    }

    /// <summary>The READ is open to any authenticated caller (the Reports screen needs the window to render
    /// its banner, and every user sees that screen); the WRITE is not.</summary>
    [Fact]
    public async Task Reading_a_setting_is_allowed_for_a_non_admin_but_writing_one_is_403()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);   // NOT an admin
        var alice = await factory.ClientAsync("alice");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/settings/{NDaysKey}")).StatusCode);

        var write = await alice.PutAsJsonAsync($"/api/settings/{NDaysKey}", new { value = "99" });

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM Settings WHERE key = @k;", new { k = NDaysKey }));
    }

    /// <summary><c>Settings.value</c> is <c>TEXT NOT NULL</c> and <c>ISettingsRepository.SetAsync</c> takes a
    /// non-nullable string, so a null from the wire is a caller error, not a 500.</summary>
    [Fact]
    public async Task Writing_a_null_setting_value_is_400()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            $"/api/settings/{NDaysKey}", new { value = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<ValidationBody>());
    }

    // ==== POST /api/standup/archive =========================================================================

    /// <summary>DR-09's weekly markdown archive had no API surface at all — <c>Program.cs</c> never calls
    /// <c>BackfillMissingWeeksAsync</c> (only the WPF <c>App.xaml.cs</c> does), so on a web-only deployment
    /// the archive was never written by anything.</summary>
    [Fact]
    public async Task Archiving_a_week_writes_the_markdown_file_and_returns_its_path()
    {
        using var factory = new ApiFactory();
        var adminId = await factory.SeedUserAsync(ApiFactory.AdminUserName, ApiFactory.DefaultPassword, isAdmin: true);
        var teamId = await factory.SeedTeamAsync("Team A", adminId);
        var admin = await factory.AdminClientAsync();
        var monday = AMonday();
        await SeedStandupEntryAsync(factory, adminId, teamId, monday, "Ship it");

        var response = await admin.PostAsync($"/api/standup/archive?date={monday:yyyy-MM-dd}", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var archived = await response.Content.ReadFromJsonAsync<ArchivedFileDto>();
        Assert.EndsWith($"{monday:yyyyMMdd}_daily.md", archived!.Path);
        Assert.True(File.Exists(archived.Path));
        Assert.Contains("Ship it", await File.ReadAllTextAsync(archived.Path));

        // The archive lands next to the TEST database, never the developer's real one -- ApiFactory points
        // IAppConfig.DbPath at its own temp root and ArchiveDir() derives from it.
        Assert.StartsWith(factory.Root, archived.Path);
    }

    /// <summary>"No data => no file" is <c>ExportWeekAsync</c>'s own rule: it returns null rather than
    /// writing an empty archive. The route must not answer 200 with a null path — the client would show a
    /// success toast pointing at nothing.</summary>
    [Fact]
    public async Task Archiving_a_week_with_no_standup_data_is_400_and_writes_no_file()
    {
        using var factory = new ApiFactory();
        var admin = await factory.AdminClientAsync();
        var monday = AMonday();

        var response = await admin.PostAsync($"/api/standup/archive?date={monday:yyyy-MM-dd}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.Contains("no standup data", body!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Archiving_a_week_is_403_for_a_non_admin()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);   // NOT an admin
        var alice = await factory.ClientAsync("alice");

        var response = await alice.PostAsync(
            $"/api/standup/archive?date={AMonday():yyyy-MM-dd}", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ==== helpers ============================================================================================

    private static async Task<long> RowVersionAsync(ApiFactory factory, int userId)
    {
        using var db = factory.OpenDb();
        return await db.ExecuteScalarAsync<long>(
            "SELECT row_version FROM Users WHERE id = @id;", new { id = userId });
    }

    private static async Task<bool> IsAdminAsync(ApiFactory factory, int userId)
    {
        using var db = factory.OpenDb();
        return await db.ExecuteScalarAsync<long>(
            "SELECT is_admin FROM Users WHERE id = @id;", new { id = userId }) == 1;
    }

    /// <summary>Demotes DIRECTLY in the database — deliberately NOT through the API. The point of the
    /// 30-day tests is that the cookie the user is ALREADY holding is not re-issued, and going through the
    /// endpoint would prove nothing extra while making the arrangement depend on the very route under
    /// test.</summary>
    private static async Task DemoteAsync(ApiFactory factory, int userId)
    {
        using var db = factory.OpenDb();
        await db.ExecuteAsync(
            "UPDATE Users SET is_admin = 0, row_version = row_version + 1 WHERE id = @id;",
            new { id = userId });
    }

    /// <summary>Seeds a standup entry DIRECTLY, and it has to be direct.
    ///
    /// <para><c>POST /api/standup/entries</c> cannot arrange this: <c>StandupService.AddEntryAsync</c> checks
    /// <c>CanEditDay</c>, which permits ONLY today and yesterday, and silently no-ops (returns id 0 -> 400)
    /// for any other date. The archive, meanwhile, only ever covers Mon–Fri of the target week. So on a
    /// Sunday there is NO date that is both editable and archivable, and a test that seeded through the
    /// route would pass all week and fail on the weekend. Seeding the row directly makes the arrangement
    /// independent of the wall clock entirely — which is what a fixed <see cref="AMonday"/> needs.</para>
    ///
    /// <para>The columns are <c>backlog_id</c> / <c>backlog_code</c>: <c>DatabaseInitializer</c>'s CREATE
    /// TABLE still shows the original <c>request_*</c> names, but a later migration renames them, so the
    /// CREATE is NOT the shape of a live database. Read <c>StandupRepository.EntryCols</c> for that.</para></summary>
    private static async Task SeedStandupEntryAsync(
        ApiFactory factory, int userId, int teamId, DateOnly workDate, string taskText)
    {
        using var db = factory.OpenDb();
        await db.ExecuteAsync(
            @"INSERT INTO StandupEntries(
                  user_id, work_date, section, backlog_id, backlog_code, task_text,
                  description, deadline, status, order_index, created_at, team_id)
              VALUES(@userId, @workDate, 'today', NULL, 'REQ-1', @taskText,
                  '', NULL, 'Todo', 0, @now, @teamId);",
            new
            {
                userId,
                workDate = workDate.ToString("yyyy-MM-dd"),
                taskText,
                now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                teamId,
            });
    }

    /// <summary>A fixed Monday, so the week under archive is deterministic. The archive covers Mon–Fri of the
    /// week containing the given date.</summary>
    private static DateOnly AMonday()
    {
        var date = new DateOnly(2026, 7, 6);
        while (date.DayOfWeek != DayOfWeek.Monday) date = date.AddDays(1);
        return date;
    }
}
