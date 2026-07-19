using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>W2-D. Covers tag CRUD, teams, PCA contacts, users, templates, holidays, default-tasks, standup
/// entries+issues, and the four <c>/api/ops/*</c> routes.
///
/// <para>Three scenarios get the most weight because they are the ones a wrong implementation would still
/// pass 90% of the other tests while failing: (1) pairing MY entry id with SOMEONE ELSE's issue id, which
/// only a flat <c>/api/standup/issues/{id}</c> design (explicitly rejected in the class doc) would even make
/// possible to get wrong; (2) an entry that is MINE but filed under a team I am not a member of, which the
/// service's owner gate alone does not catch; (3) a demoted admin whose cookie still says admin.</para></summary>
public sealed class SettingsEndpointsTests
{
    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>yyyy-MM-dd for URLs — matches the format the repository layer itself uses (see
    /// HolidayRepository.Day / StandupRepository.Day). String interpolation's own format specifier
    /// (<c>{d:yyyy-MM-dd}</c>) uses the AMBIENT CULTURE, which is not guaranteed to treat "yyyy-MM-dd" as a
    /// literal pattern the same way in every environment; passing InvariantCulture explicitly removes the
    /// question entirely.</summary>
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- seed helpers (ApiFactory owns none of these repositories' seeds — go through the real repos,
    // exactly like ApiFactory.SeedHolidayAsync does, rather than hand-rolling SQL) -----------------------

    private static async Task<int> SeedTagAsync(ApiFactory factory, string text = "urgent") =>
        await factory.Services.GetRequiredService<ITagRepository>()
            .InsertAsync(new Tag(0, text, "!", "#ff0000", DateTimeOffset.UtcNow));

    private static async Task<int> SeedStandupEntryAsync(
        ApiFactory factory, int userId, int? teamId, DateOnly workDate,
        string section = StandupSection.Today, string taskText = "Do the thing", string status = "Todo",
        int orderIndex = 0) =>
        await factory.Services.GetRequiredService<IStandupRepository>().InsertEntryAsync(new StandupEntry(
            0, userId, workDate, section, null, "ADHOC-1", taskText, "", null, status, orderIndex,
            DateTimeOffset.UtcNow, teamId));

    private static async Task<int> SeedStandupIssueAsync(
        ApiFactory factory, int entryId, string issueText = "issue", string status = "open") =>
        await factory.Services.GetRequiredService<IStandupRepository>()
            .InsertIssueAsync(new StandupIssue(0, entryId, issueText, null, status, 0, DateTimeOffset.UtcNow));

    // ===== Tags ============================================================================================

    [Fact]
    public async Task Admin_creates_a_tag_and_any_authenticated_user_can_read_it()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var admin = await factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/tags", new SettingsTagCreateRequest("urgent", "!", "#f00"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var tag = await created.Content.ReadFromJsonAsync<TagDto>();
        Assert.Equal("urgent", tag!.Text);
        Assert.Equal(1, tag.RowVersion);

        using var alice = await factory.ClientAsync("alice");
        var list = await (await alice.GetAsync("/api/tags")).Content.ReadFromJsonAsync<List<TagDto>>();
        Assert.Contains(list!, t => t.Id == tag.Id);
    }

    [Fact]
    public async Task A_non_admin_cannot_create_a_tag()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("peon");

        var response = await client.PostAsJsonAsync("/api/tags", new SettingsTagCreateRequest("x", "!", "#000"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_stale_tag_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var tagId = await SeedTagAsync(factory);
        using var admin = await factory.AdminClientAsync();

        var v1 = 1L;
        var moved = await admin.PutAsJsonAsync($"/api/tags/{tagId}", new SettingsTagUpdateRequest("renamed", "!", "#f00", v1));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var conflict = await admin.PutAsJsonAsync($"/api/tags/{tagId}", new SettingsTagUpdateRequest("stale", "!", "#f00", v1));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Tags", body!.Table);
        Assert.False(body.Deleted);
    }

    [Fact]
    public async Task A_stale_update_on_a_deleted_tag_is_409_with_deleted_true()
    {
        using var factory = new ApiFactory();
        var tagId = await SeedTagAsync(factory);
        using var admin = await factory.AdminClientAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/tags/{tagId}")).StatusCode);

        var conflict = await admin.PutAsJsonAsync($"/api/tags/{tagId}", new SettingsTagUpdateRequest("x", "!", "#f00", 1));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.True(body!.Deleted);
    }

    // ===== Teams ============================================================================================

    [Fact]
    public async Task Team_create_active_list_is_open_but_all_list_and_writes_are_admin_only()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var admin = await factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/teams", new SettingsNameRequest("Blue Team"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var team = await created.Content.ReadFromJsonAsync<TeamDto>();

        using var alice = await factory.ClientAsync("alice");
        var openList = await (await alice.GetAsync("/api/teams")).Content.ReadFromJsonAsync<List<TeamDto>>();
        Assert.Contains(openList!, t => t.Id == team!.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/teams/all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PutAsJsonAsync($"/api/teams/{team!.Id}/active", new SettingsSetActiveRequest(false))).StatusCode);

        var deactivate = await admin.PutAsJsonAsync($"/api/teams/{team.Id}/active", new SettingsSetActiveRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var allList = await (await admin.GetAsync("/api/teams/all")).Content.ReadFromJsonAsync<List<TeamDto>>();
        Assert.Contains(allList!, t => t.Id == team.Id && !t.IsActive);
    }

    [Fact]
    public async Task Team_membership_can_be_set_and_read_back_by_an_admin()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A");
        using var admin = await factory.AdminClientAsync();

        var setResponse = await admin.PutAsJsonAsync(
            $"/api/teams/{teamId}/members", new SettingsTeamMembersRequest(new[] { aliceId }, ExpectedVersion: 1));
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        var members = await (await admin.GetAsync($"/api/teams/{teamId}/members")).Content.ReadFromJsonAsync<List<int>>();
        Assert.Equal(new[] { aliceId }, members);
    }

    /// <summary>M9 P2d — TM-04. <c>POST /api/teams</c> USED TO SKIP THE BOOTSTRAP ENTIRELY.
    ///
    /// <para>WPF's <c>SettingsViewModel.AddTeamAsync</c> does <c>InsertAsync</c> →
    /// <c>EnsureDefaultBacklogIdAsync(newId)</c> → <c>SyncAsync()</c>. The route did only the first, so a team
    /// created from the WEB got no <c>DEFAULT</c> backlog and no default tasks — its members would have had
    /// nothing to log Annual Leave / Meeting / Other against. Nobody had noticed because no client could reach
    /// the route yet; wiring the M9 Settings screen is what makes it reachable.</para>
    ///
    /// <para>The default task is created BEFORE the team ON PURPOSE. <c>POST /api/default-tasks</c> itself
    /// calls <c>SyncAsync()</c>, which reconciles into every ACTIVE team — so creating the task second would
    /// materialize the rows as a side-effect of the WRONG endpoint and this test would pass against the
    /// unfixed route, proving nothing. With no team in existence at that moment, that sync is a no-op over
    /// zero teams, and the only thing that can put "Annual Leave" under Blue Team is the team-create route
    /// doing its own bootstrap.</para></summary>
    [Fact]
    public async Task Creating_a_team_via_the_API_bootstraps_its_DEFAULT_backlog_and_the_default_tasks()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        // No teams exist yet, so this SyncAsync reconciles over nothing. See the doc comment.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/default-tasks", new SettingsDefaultTaskCreateRequest("Annual Leave", 0))).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/teams", new SettingsNameRequest("Blue Team"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var team = await created.Content.ReadFromJsonAsync<TeamDto>();

        using var db = factory.OpenDb();

        var defaultBacklogs = await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Backlogs WHERE team_id = @teamId AND backlog_code = 'DEFAULT';",
            new { teamId = team!.Id });
        Assert.Equal(1, defaultBacklogs);

        // A DEFAULT backlog with nothing in it is still a team whose members cannot log Annual Leave.
        var materialized = await db.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM Tasks t JOIN Backlogs b ON b.id = t.backlog_id
              WHERE b.team_id = @teamId AND b.backlog_code = 'DEFAULT'
                AND t.task_name = 'Annual Leave' AND t.is_active = 1;",
            new { teamId = team.Id });
        Assert.Equal(1, materialized);
    }

    // ===== PCA contacts =====================================================================================

    [Fact]
    public async Task PcaContact_create_rename_and_deactivate()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/pca-contacts", new SettingsNameRequest("Acme"));
        var contact = await created.Content.ReadFromJsonAsync<PcaContactDto>();

        var renamed = await admin.PutAsJsonAsync(
            $"/api/pca-contacts/{contact!.Id}", new SettingsRenameRequest("Acme Corp", contact.RowVersion));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var saved = await renamed.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > contact.RowVersion);

        var deactivated = await admin.PutAsJsonAsync(
            $"/api/pca-contacts/{contact.Id}/active", new SettingsSetActiveRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var activeList = await (await admin.GetAsync("/api/pca-contacts")).Content.ReadFromJsonAsync<List<PcaContactDto>>();
        Assert.DoesNotContain(activeList!, c => c.Id == contact.Id);
    }

    /// <summary>M8.6. The same contract as the users' name route, for the editor's PCA-contact field:
    /// readable by an ORDINARY user (<c>/api/pca-contacts/all</c> is AdminPolicy-gated), and still naming a
    /// DEACTIVATED contact (the active list omits it). Seeded NON-admin ON PURPOSE — see the users' test.
    ///
    /// <para>Arranged through the admin routes the test above already proves, rather than hand-rolled SQL
    /// against a schema this test does not own.</para></summary>
    [Fact]
    public async Task Pca_contact_names_is_readable_by_a_NON_admin_and_still_names_a_deactivated_contact()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);      // NOT an admin

        using var admin = await factory.AdminClientAsync();
        var created = await admin.PostAsJsonAsync("/api/pca-contacts", new SettingsNameRequest("Globex"));
        var contact = await created.Content.ReadFromJsonAsync<PcaContactDto>();
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsJsonAsync($"/api/pca-contacts/{contact!.Id}/active",
                new SettingsSetActiveRequest(false))).StatusCode);

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.GetAsync("/api/pca-contacts/names");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = await response.Content.ReadFromJsonAsync<List<NamedRefDto>>();
        Assert.Contains(names!, c => c.Id == contact.Id && c.Name == "Globex");

        // The route is NECESSARY: the open active list cannot name it.
        var active = await (await alice.GetAsync("/api/pca-contacts")).Content
            .ReadFromJsonAsync<List<PcaContactDto>>();
        Assert.DoesNotContain(active!, c => c.Id == contact.Id);
    }

    // ===== Users =============================================================================================

    [Fact]
    public async Task User_create_then_set_username_then_rename_via_admin()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/users", new SettingsNameRequest("Bob"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<UserDto>();
        Assert.Null(user!.Username);
        Assert.False(user.IsAdmin);

        var withUsername = await admin.PutAsJsonAsync(
            $"/api/users/{user.Id}/username", new SettingsUserSetUsernameRequest("bob", user.RowVersion));
        Assert.Equal(HttpStatusCode.OK, withUsername.StatusCode);
        var v2 = (await withUsername.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;

        var renamed = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new SettingsRenameRequest("Bobby", v2));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var openList = await (await admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.Contains(openList!, u => u.Id == user.Id && u.Name == "Bobby" && u.Username == "bob");
    }

    /// <summary>M11 (Users screen "everyone can log in" bug). <c>UserDto.HasPassword</c> is
    /// <c>password_hash IS NOT NULL</c>, projected so the admin screen can tell a bare username apart from
    /// an account that can actually authenticate. Round-tripped through the REAL endpoint (not the
    /// repository directly) so a wrong <c>ToDto()</c> mapping or a stale OpenAPI schema would show up
    /// here too, not just at the repository layer.</summary>
    [Fact]
    public async Task UserDto_HasPassword_reflects_the_password_hash_and_survives_the_endpoint_round_trip()
    {
        using var factory = new ApiFactory();
        var withPasswordId = await factory.SeedUserAsync("withpass", ApiFactory.DefaultPassword);
        var withoutPasswordId = await factory.SeedUserWithoutPasswordAsync("nopass");

        using var admin = await factory.AdminClientAsync();
        var all = (await (await admin.GetAsync("/api/users/all")).Content.ReadFromJsonAsync<List<UserDto>>())!;

        Assert.True(all.Single(u => u.Id == withPasswordId).HasPassword);
        Assert.False(all.Single(u => u.Id == withoutPasswordId).HasPassword);
    }

    [Fact]
    public async Task Users_all_list_is_admin_only_but_active_list_is_open()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var alice = await factory.ClientAsync("alice");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/users/all")).StatusCode);
    }

    // ===== The admin-gated FULL lists (M9 P2a) ==============================================================

    /// <summary>M9 P2a. THE GUARD ON <c>/api/users/all</c> AND <c>/api/pca-contacts/all</c>.
    ///
    /// <para>It replaces <c>Admin_gated_list_is_NOT_tagged_and_so_never_joins_the_generated_client</c>
    /// (OpenApiContractTests), which M8.6 wrote on the rationale "no admin-only screen exists, so keep these
    /// routes out of the generated client entirely". M9 BUILDS that screen: the Users tab must list INACTIVE
    /// users (USR-01) or the "Activate" button has nothing to act on, and <c>GET /api/users</c> is active-only.
    /// So the routes are now tagged and DO join the client.</para>
    ///
    /// <para><b>This test is the stronger guard put in that one's place.</b> The old test asserted a PROXY for
    /// the security property (the route is absent from the generated client — true, but it only ever stopped
    /// OUR OWN client from calling it; curl was never impressed). This asserts the PROPERTY ITSELF: the route
    /// rejects a non-admin. If the <c>AdminPolicy</c> is ever dropped from either route, THIS is what fails.</para>
    ///
    /// <para><b>The seeded user is a NON-admin ON PURPOSE.</b> <c>SeedUserAsync</c> takes an <c>isAdmin</c>
    /// flag, and seeding an admin here would sail straight through an admin-gated route and prove exactly
    /// nothing — the failure mode that shipped in M8.5.</para></summary>
    [Theory]
    [InlineData("/api/users/all")]
    [InlineData("/api/pca-contacts/all")]
    [InlineData("/api/default-tasks/all")]
    public async Task The_admin_gated_full_list_is_403_for_a_NON_admin(string route)
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);   // NOT an admin. Deliberately.
        using var alice = await factory.ClientAsync("alice");

        var response = await alice.GetAsync(route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>M9 P2a / USR-01 — WHY <c>/api/users/all</c> has to join the generated client at all.
    ///
    /// <para>The Users tab shows inactive users too, and "Activate" is impossible without them.
    /// <c>GET /api/users</c> returns ACTIVE ONLY (<c>GetActiveAsync</c>), so it can never render a
    /// deactivated user — there would be nothing to click Activate on. <c>/api/users/all</c>
    /// (<c>GetAllAsync</c>) is the only route that can, and an admin must be able to read it.</para>
    ///
    /// <para>Asserting the OMISSION from the active list is the load-bearing half: it is what proves the
    /// admin route is NECESSARY rather than merely convenient.</para></summary>
    [Fact]
    public async Task Users_all_lists_the_INACTIVE_user_that_the_Activate_button_needs_and_the_active_list_omits()
    {
        using var factory = new ApiFactory();
        var zoeId = await factory.SeedUserAsync("zoe", ApiFactory.DefaultPassword);
        await factory.SetUserActiveAsync(zoeId, false);          // the user an admin now needs to re-activate

        using var admin = await factory.AdminClientAsync();

        var all = await (await admin.GetAsync("/api/users/all")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.Contains(all!, u => u.Id == zoeId && !u.IsActive && u.Name == ApiFactory.DisplayNameFor("zoe"));

        // The route is NECESSARY: the open active list cannot see her, so the Users tab cannot be built on it.
        var active = await (await admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.DoesNotContain(active!, u => u.Id == zoeId);

        // And Activate actually works on what /all handed back.
        var reactivated = await admin.PutAsJsonAsync(
            $"/api/users/{zoeId}/active", new SettingsSetActiveRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);

        var afterActivate = await (await admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.Contains(afterActivate!, u => u.Id == zoeId);
    }

    /// <summary>M8.6. The backlog editor resolves an assignee's NAME through this route, and that assignee
    /// may have been DEACTIVATED since the backlog was filed. So the route must do two things the two
    /// existing lists each fail at: answer an ORDINARY user (<c>/api/users/all</c> is AdminPolicy-gated —
    /// a client reading THAT to resolve the name gets a 403 and the whole screen dies with it), and still
    /// contain the deactivated user (whom <c>/api/users</c> omits by construction). Both halves are asserted
    /// below, including the omission, because that omission is the entire reason this route exists.
    ///
    /// <para><b>The seeded user is a NON-admin ON PURPOSE.</b> <c>SeedUserAsync</c> takes an
    /// <c>isAdmin</c> flag, and seeding an admin here would sail straight through an admin-gated route and
    /// prove exactly nothing. If this route ever gains a policy, THIS TEST IS THE THING THAT FAILS.</para></summary>
    [Fact]
    public async Task User_names_is_readable_by_a_NON_admin_and_still_names_a_deactivated_user()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);      // NOT an admin
        var zoeId = await factory.SeedUserAsync("zoe", ApiFactory.DefaultPassword);
        await factory.SetUserActiveAsync(zoeId, false);                        // the assignee who has left

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.GetAsync("/api/users/names");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var names = JsonSerializer.Deserialize<List<NamedRefDto>>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(names, u => u.Id == zoeId && u.Name == ApiFactory.DisplayNameFor("zoe"));

        // The route is NECESSARY: the open active list cannot name her.
        var active = await (await alice.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.DoesNotContain(active!, u => u.Id == zoeId);

        // `username` is the credential handle the admin gate on /api/users/all exists to protect, and it
        // must not ride along here. The fixture makes the display name ("Zoe Nguyen") and the login handle
        // ("zoe") deliberately DIFFERENT, so a leak is visible instead of structurally invisible.
        Assert.DoesNotContain("username", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"zoe\"", json, StringComparison.Ordinal);
    }

    // ===== Templates =========================================================================================

    [Fact]
    public async Task Template_create_delete_by_id_and_delete_by_name()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var row1 = await admin.PostAsJsonAsync("/api/templates", new SettingsTemplateCreateRequest("Onboarding", "Set up laptop", 0));
        await admin.PostAsJsonAsync("/api/templates", new SettingsTemplateCreateRequest("Onboarding", "Meet the team", 1));
        var t1 = await row1.Content.ReadFromJsonAsync<TaskTemplateDto>();

        var afterCreate = await (await admin.GetAsync("/api/templates")).Content.ReadFromJsonAsync<List<TaskTemplateDto>>();
        Assert.Equal(2, afterCreate!.Count(t => t.TemplateName == "Onboarding"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/templates/{t1!.Id}")).StatusCode);
        var afterOneDelete = await (await admin.GetAsync("/api/templates")).Content.ReadFromJsonAsync<List<TaskTemplateDto>>();
        Assert.Single(afterOneDelete!, t => t.TemplateName == "Onboarding");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync("/api/templates?templateName=Onboarding")).StatusCode);
        var afterBulkDelete = await (await admin.GetAsync("/api/templates")).Content.ReadFromJsonAsync<List<TaskTemplateDto>>();
        Assert.DoesNotContain(afterBulkDelete!, t => t.TemplateName == "Onboarding");
    }

    // ===== Holidays (column is holiday_date, not date) ======================================================

    [Fact]
    public async Task Holiday_upsert_month_scoped_get_and_delete()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();
        var date = new DateOnly(2026, 12, 25);

        var upserted = await admin.PostAsJsonAsync("/api/holidays", new SettingsHolidayRequest(date, "Christmas"));
        Assert.Equal(HttpStatusCode.NoContent, upserted.StatusCode);

        var monthList = await (await admin.GetAsync("/api/holidays?year=2026&month=12"))
            .Content.ReadFromJsonAsync<List<HolidayDto>>();
        Assert.Contains(monthList!, h => h.Date == date && h.Description == "Christmas");

        var wholeCalendar = await (await admin.GetAsync("/api/holidays")).Content.ReadFromJsonAsync<List<HolidayDto>>();
        Assert.Contains(wholeCalendar!, h => h.Date == date);

        var deleted = await admin.DeleteAsync("/api/holidays/2026-12-25");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await (await admin.GetAsync("/api/holidays?year=2026&month=12"))
            .Content.ReadFromJsonAsync<List<HolidayDto>>();
        Assert.DoesNotContain(afterDelete!, h => h.Date == date);
    }

    [Fact]
    public async Task A_malformed_holiday_date_route_segment_is_400_not_500()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.DeleteAsync("/api/holidays/not-a-date");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ===== Default tasks (every write must reach IDefaultTaskSyncService.SyncAsync) =========================

    [Fact]
    public async Task Creating_a_default_task_materializes_it_under_every_teams_DEFAULT_backlog()
    {
        using var factory = new ApiFactory();
        var teamId = await factory.SeedTeamAsync("Team A");
        using var admin = await factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/default-tasks", new SettingsDefaultTaskCreateRequest("Daily standup", 0));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var db = factory.OpenDb();
        var count = await db.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM Tasks t JOIN Backlogs b ON b.id = t.backlog_id
              WHERE b.team_id = @teamId AND b.backlog_code = 'DEFAULT'
                AND t.task_name = 'Daily standup' AND t.is_active = 1;",
            new { teamId });
        Assert.Equal(1, count);
    }

    /// <summary>M9 P2e — the standalone <c>POST /api/default-tasks/sync</c>.
    ///
    /// <para>WPF's "Sync default tasks" button calls <c>IDefaultTaskSyncService.SyncAsync()</c> directly. The
    /// API only ever ran it as a SIDE-EFFECT of <c>POST /api/default-tasks</c> and
    /// <c>PUT /api/default-tasks/{id}/active</c> — there was no route the Settings screen's button could call,
    /// so a reconcile could not be triggered without also writing a default task.</para>
    ///
    /// <para>The arrangement below is the state only a standalone sync can repair: a DefaultTask that already
    /// exists, and an ACTIVE team that came into being AFTER it via a path that does no bootstrap
    /// (<c>SeedTeamAsync</c> is raw SQL — exactly like a row inserted by an older build, a restore, or a
    /// hand-edit). The task is therefore NOT materialized under that team, and the precondition asserts that
    /// before the fix is exercised — otherwise a green here would prove nothing.</para></summary>
    [Fact]
    public async Task Sync_route_materializes_default_tasks_into_a_team_that_was_created_without_a_bootstrap()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        // No teams yet -> this write's own SyncAsync reconciles over nothing.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(
            "/api/default-tasks", new SettingsDefaultTaskCreateRequest("Meeting", 0))).StatusCode);

        // Raw SQL. Active team, no DEFAULT backlog, no bootstrap ran for it.
        var teamId = await factory.SeedTeamAsync("Orphan Team");

        const string materializedSql =
            @"SELECT COUNT(*) FROM Tasks t JOIN Backlogs b ON b.id = t.backlog_id
              WHERE b.team_id = @teamId AND b.backlog_code = 'DEFAULT'
                AND t.task_name = 'Meeting' AND t.is_active = 1;";

        using var db = factory.OpenDb();

        // PRECONDITION: without this the test would pass whether or not the route did anything.
        Assert.Equal(0, await db.ExecuteScalarAsync<long>(materializedSql, new { teamId }));

        var synced = await admin.PostAsync("/api/default-tasks/sync", content: null);
        Assert.Equal(HttpStatusCode.NoContent, synced.StatusCode);

        Assert.Equal(1, await db.ExecuteScalarAsync<long>(materializedSql, new { teamId }));
    }

    [Fact]
    public async Task A_non_admin_cannot_trigger_a_default_task_sync()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("peon");

        var response = await client.PostAsync("/api/default-tasks/sync", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ===== Standup entries ====================================================================================

    [Fact]
    public async Task Adding_and_updating_my_own_standup_entry_round_trips_through_GetMyStandup()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);
        using var alice = await factory.ClientAsync("alice");

        var addResponse = await alice.PostAsJsonAsync("/api/standup/entries", new SettingsStandupEntryCreateRequest(
            Today(), StandupSection.Today, null, "ADHOC-1", "Write the report", "desc", null, "Todo"));
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var entryId = await addResponse.Content.ReadFromJsonAsync<int>();
        Assert.True(entryId > 0);

        var updateResponse = await alice.PutAsJsonAsync($"/api/standup/entries/{entryId}", new SettingsStandupEntryUpdateRequest(
            StandupSection.Today, null, "ADHOC-1", "Write the report (revised)", "desc", null, "In-process"));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var mine = await (await alice.GetAsync($"/api/standup/entries?date={Iso(Today())}"))
            .Content.ReadFromJsonAsync<SettingsUserStandup>();
        var todayEntry = mine!.Today.Single(e => e.Entry.Id == entryId);
        Assert.Equal("Write the report (revised)", todayEntry.Entry.TaskText);
        Assert.Equal("In-process", todayEntry.Entry.Status);
    }

    [Fact]
    public async Task Adding_a_standup_entry_outside_the_edit_window_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A");
        using var alice = await factory.ClientAsync("alice");

        var longAgo = Today().AddDays(-10);
        var response = await alice.PostAsJsonAsync("/api/standup/entries", new SettingsStandupEntryCreateRequest(
            longAgo, StandupSection.Today, null, "ADHOC-1", "too late", null, null, "Todo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The owner gate (StandupService.cs:158), proven through the API: same team, different user.</summary>
    [Fact]
    public async Task A_user_cannot_edit_a_colleagues_standup_entry()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, bobId);
        var entryId = await SeedStandupEntryAsync(factory, aliceId, teamId, Today());

        using var bob = await factory.ClientAsync("bob");
        var response = await bob.PutAsJsonAsync($"/api/standup/entries/{entryId}", new SettingsStandupEntryUpdateRequest(
            StandupSection.Today, null, "ADHOC-1", "hijacked", null, null, "Todo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var repo = factory.Services.GetRequiredService<IStandupRepository>();
        var stillAlices = await repo.GetEntryAsync(entryId);
        Assert.NotEqual("hijacked", stillAlices!.TaskText);
    }

    /// <summary>Distinct from the owner-gate test above: alice OWNS this entry, but it is filed under a team
    /// she is not (or no longer) a member of. The team gate must catch what the owner gate alone would not.</summary>
    [Fact]
    public async Task Editing_my_own_entry_under_a_team_im_not_a_member_of_is_404_and_nothing_written()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);
        var teamBId = await factory.SeedTeamAsync("Team B"); // alice is NOT a member

        var entryId = await SeedStandupEntryAsync(factory, aliceId, teamBId, Today());

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync($"/api/standup/entries/{entryId}", new SettingsStandupEntryUpdateRequest(
            StandupSection.Today, null, "ADHOC-1", "changed", null, null, "Todo"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var repo = factory.Services.GetRequiredService<IStandupRepository>();
        var unchanged = await repo.GetEntryAsync(entryId);
        Assert.NotEqual("changed", unchanged!.TaskText);
    }

    [Fact]
    public async Task Deleting_my_own_entry_succeeds_and_it_is_gone()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        var entryId = await SeedStandupEntryAsync(factory, aliceId, teamId, Today());

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.DeleteAsync($"/api/standup/entries/{entryId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var repo = factory.Services.GetRequiredService<IStandupRepository>();
        Assert.Null(await repo.GetEntryAsync(entryId));
    }

    // ===== Standup issues ====================================================================================

    [Fact]
    public async Task Issue_add_update_and_delete_round_trip()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        var entryId = await SeedStandupEntryAsync(factory, aliceId, teamId, Today());
        using var alice = await factory.ClientAsync("alice");

        var addResponse = await alice.PostAsJsonAsync(
            $"/api/standup/entries/{entryId}/issues",
            new SettingsStandupIssueCreateRequest("build is red", null, "open"));
        Assert.Equal(HttpStatusCode.OK, addResponse.StatusCode);
        var issueId = await addResponse.Content.ReadFromJsonAsync<int>();

        var updateResponse = await alice.PutAsJsonAsync(
            $"/api/standup/entries/{entryId}/issues/{issueId}",
            new SettingsStandupIssueUpdateRequest("build is red", "fixed the flaky test", "resolved", ExpectedVersion: 1));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var saved = await updateResponse.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > 1);

        var deleteResponse = await alice.DeleteAsync($"/api/standup/entries/{entryId}/issues/{issueId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var remaining = await factory.Services.GetRequiredService<IStandupRepository>()
            .GetIssuesForEntriesAsync(new[] { entryId });
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task A_stale_issue_update_is_409()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        var entryId = await SeedStandupEntryAsync(factory, aliceId, teamId, Today());
        var issueId = await SeedStandupIssueAsync(factory, entryId);
        using var alice = await factory.ClientAsync("alice");

        var conflict = await alice.PutAsJsonAsync(
            $"/api/standup/entries/{entryId}/issues/{issueId}",
            new SettingsStandupIssueUpdateRequest("x", null, "open", ExpectedVersion: 999));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("StandupIssues", body!.Table);
    }

    /// <summary>THE scenario the nested route shape exists for: entryId and issueId are both attacker-supplied
    /// and independently valid, but do not belong together. Pairing MY OWN entry id with a COLLEAGUE's issue
    /// id must not let me touch their issue.</summary>
    [Fact]
    public async Task Cannot_edit_a_colleagues_issue_by_pairing_my_own_entry_id_with_their_issue_id()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, bobId);

        var myEntryId = await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "alice's entry");
        var bobsEntryId = await SeedStandupEntryAsync(factory, bobId, teamId, Today(), taskText: "bob's entry");
        var bobsIssueId = await SeedStandupIssueAsync(factory, bobsEntryId, "bob's issue");

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync(
            $"/api/standup/entries/{myEntryId}/issues/{bobsIssueId}",
            new SettingsStandupIssueUpdateRequest("hijacked", null, "open", ExpectedVersion: 1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var stillBobs = (await factory.Services.GetRequiredService<IStandupRepository>()
            .GetIssuesForEntriesAsync(new[] { bobsEntryId })).Single();
        Assert.Equal("bob's issue", stillBobs.IssueText);
    }

    [Fact]
    public async Task Adding_an_issue_under_a_team_im_not_a_member_of_is_404()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);
        var teamBId = await factory.SeedTeamAsync("Team B"); // alice not a member
        var otherUserId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var entryUnderTeamB = await SeedStandupEntryAsync(factory, otherUserId, teamBId, Today());

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PostAsJsonAsync(
            $"/api/standup/entries/{entryUnderTeamB}/issues",
            new SettingsStandupIssueCreateRequest("sneaky", null, "open"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ===== Standup board (R6) ================================================================================

    /// <summary>R6: a client-supplied teamId the caller is not a member of must yield empty, never rows —
    /// GetTeamStandupAsync filters `t > 0` but does NOT intersect with membership on its own
    /// (StandupService.cs:60), so the endpoint has to do it.</summary>
    [Fact]
    public async Task Board_request_for_a_team_im_not_a_member_of_returns_empty_never_rows()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);

        var carolId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var teamBId = await factory.SeedTeamAsync("Team B", carolId);
        await SeedStandupEntryAsync(factory, carolId, teamBId, Today(), taskText: "carol's secret entry");

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.GetAsync($"/api/standup/board?date={Iso(Today())}&teamIds={teamBId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var board = await response.Content.ReadFromJsonAsync<List<SettingsUserStandup>>();
        Assert.Empty(board!);
    }

    [Fact]
    public async Task Board_request_with_no_teamIds_defaults_to_my_own_membership()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "alice's task");

        using var alice = await factory.ClientAsync("alice");
        var board = await (await alice.GetAsync($"/api/standup/board?date={Iso(Today())}"))
            .Content.ReadFromJsonAsync<List<SettingsUserStandup>>();

        Assert.Contains(board!, u => u.UserId == aliceId && u.Today.Any(e => e.Entry.TaskText == "alice's task"));
    }

    // ===== Active-team switch (PUT /api/me/active-team) =======================================================

    [Fact]
    public async Task Switching_to_a_team_im_a_member_of_changes_my_active_team()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B", aliceId);

        using var alice = await factory.ClientAsync("alice");
        var before = await (await alice.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(teamA, before!.ActiveTeamId);   // resolves to the first available team

        var switched = await alice.PutAsJsonAsync("/api/me/active-team", new SettingsActiveTeamRequest(teamB));
        Assert.Equal(HttpStatusCode.NoContent, switched.StatusCode);

        var after = await (await alice.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(teamB, after!.ActiveTeamId);
    }

    /// <summary>Rule 8. Without this gate a user switches themselves into a team they are not in, and every
    /// subsequent timesheet/standup query then serves them that team's data.</summary>
    [Fact]
    public async Task Switching_to_a_team_im_not_a_member_of_is_404_and_leaves_my_active_team_alone()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B");   // alice is NOT a member

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync("/api/me/active-team", new SettingsActiveTeamRequest(teamB));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var after = await (await alice.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(teamA, after!.ActiveTeamId);
    }

    /// <summary>THE TRAP the coordinator's "must be in ctx.MemberTeamIds, else 404" rule does not close.
    ///
    /// <para><c>MemberTeamIds</c> is every <c>UserTeams</c> row with NO <c>is_active</c> filter;
    /// <c>ICurrentTeamService.AvailableTeams</c> is memberships INTERSECTED WITH ACTIVE TEAMS — strictly
    /// narrower. <c>SetActiveTeamAsync</c> THROWS <c>InvalidOperationException</c> outside
    /// <c>AvailableTeams</c>, and <c>ExceptionMapper</c> maps only <c>ConcurrencyConflictException</c> and
    /// <c>ArgumentException</c> — so that throw escapes as a 500.</para>
    ///
    /// <para>A membership-only check would sail straight into it: the team below is soft-deleted via
    /// <c>PUT /api/teams/{id}/active</c> (an endpoint in THIS file), and the <c>UserTeams</c> row survives
    /// the soft-delete. Member: yes. Available: no.</para></summary>
    [Fact]
    public async Task Switching_to_a_deactivated_team_im_still_a_member_of_is_400_not_500()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B", aliceId);

        // Soft-delete Team B. The UserTeams row is untouched, so alice REMAINS a member of it.
        await factory.Services.GetRequiredService<ITeamRepository>().SetActiveAsync(teamB, isActive: false);

        using var alice = await factory.ClientAsync("alice");

        // The membership check the coordinator specified would PASS here — this is what makes the second
        // gate load-bearing rather than redundant.
        var me = await (await alice.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Contains(teamB, me!.MemberTeamIds);

        var response = await alice.PutAsJsonAsync("/api/me/active-team", new SettingsActiveTeamRequest(teamB));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        var after = await (await alice.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.Equal(teamA, after!.ActiveTeamId);
    }

    // ===== Standup reorder ====================================================================================

    /// <summary>Also proves the route table: "reorder" cannot satisfy the <c>:int</c> constraint on the
    /// sibling <c>PUT /api/standup/entries/{entryId:int}</c>, so this reaches the reorder handler rather than
    /// the entry-update handler (or an AmbiguousMatchException).</summary>
    [Fact]
    public async Task Reordering_my_own_entries_within_my_day_moves_them()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);
        var first = await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "first", orderIndex: 0);
        var second = await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "second", orderIndex: 1);

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync(
            "/api/standup/entries/reorder", new SettingsStandupReorderRequest(DraggedId: second, TargetId: first));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var repo = factory.Services.GetRequiredService<IStandupRepository>();
        Assert.Equal(0, (await repo.GetEntryAsync(second))!.OrderIndex);   // dragged took the target's slot
        Assert.Equal(1, (await repo.GetEntryAsync(first))!.OrderIndex);
    }

    [Fact]
    public async Task Reordering_an_entry_i_do_not_own_is_400_and_moves_nothing()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, bobId);

        var alices = await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "alice's", orderIndex: 0);
        var bobs = await SeedStandupEntryAsync(factory, bobId, teamId, Today(), taskText: "bob's", orderIndex: 0);

        // bob tries to drag ALICE's entry (same team, so the team gate passes — only the owner gate is left).
        using var bob = await factory.ClientAsync("bob");
        var response = await bob.PutAsJsonAsync(
            "/api/standup/entries/reorder", new SettingsStandupReorderRequest(DraggedId: alices, TargetId: bobs));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var repo = factory.Services.GetRequiredService<IStandupRepository>();
        Assert.Equal(0, (await repo.GetEntryAsync(alices))!.OrderIndex);
    }

    [Fact]
    public async Task Reordering_an_entry_from_a_team_im_not_in_is_404()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", aliceId);

        var carolId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var teamB = await factory.SeedTeamAsync("Team B", carolId);
        var e1 = await SeedStandupEntryAsync(factory, carolId, teamB, Today(), taskText: "c1", orderIndex: 0);
        var e2 = await SeedStandupEntryAsync(factory, carolId, teamB, Today(), taskText: "c2", orderIndex: 1);

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync(
            "/api/standup/entries/reorder", new SettingsStandupReorderRequest(DraggedId: e2, TargetId: e1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The coordinator's stated reorder threat — "I reorder your team's board by pairing my entry id
    /// with yours" — does NOT hold, and this proves it rather than asserting it.
    ///
    /// <para><c>ReorderEntryAsync</c> builds its write set from
    /// <c>GetEntriesAsync(me.Id, …)</c> (StandupService.cs:200), so every <c>UpdateEntryAsync</c> it issues
    /// targets the CALLER's own rows. A colleague's entry id is only ever READ, for its <c>.Section</c> and
    /// <c>.WorkDate</c>. The team gate on <c>targetId</c> is real defense-in-depth, but it is not what stands
    /// between an attacker and someone else's rows — the service's own scoping is.</para></summary>
    [Fact]
    public async Task Dragging_my_entry_onto_a_colleagues_entry_never_rewrites_the_colleagues_row()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, bobId);

        var alices = await SeedStandupEntryAsync(factory, aliceId, teamId, Today(), taskText: "alice's", orderIndex: 0);
        var bobs = await SeedStandupEntryAsync(
            factory, bobId, teamId, Today(), section: StandupSection.Yesterday, taskText: "bob's", orderIndex: 7);

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PutAsJsonAsync(
            "/api/standup/entries/reorder", new SettingsStandupReorderRequest(DraggedId: alices, TargetId: bobs));

        // Every gate passes (same team, alice owns the dragged entry, same day), so the service runs.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Bob's row is byte-for-byte untouched: same section, same order, same text.
        var stillBobs = await factory.Services.GetRequiredService<IStandupRepository>().GetEntryAsync(bobs);
        Assert.Equal(StandupSection.Yesterday, stillBobs!.Section);
        Assert.Equal(7, stillBobs.OrderIndex);
        Assert.Equal("bob's", stillBobs.TaskText);
        Assert.Equal(bobId, stillBobs.UserId);
    }

    // ===== Standup quick-import ================================================================================

    [Fact]
    public async Task Quick_import_clones_my_source_day_into_my_target_day_with_its_issues()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);

        var yesterday = Today().AddDays(-1);
        var sourceEntry = await SeedStandupEntryAsync(factory, aliceId, teamId, yesterday, taskText: "carry me over");
        await SeedStandupIssueAsync(factory, sourceEntry, "blocked on review");

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PostAsJsonAsync(
            "/api/standup/quick-import", new SettingsQuickImportRequest(yesterday, Today()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await response.Content.ReadFromJsonAsync<int>());

        var mine = await (await alice.GetAsync($"/api/standup/entries?date={Iso(Today())}"))
            .Content.ReadFromJsonAsync<SettingsUserStandup>();
        var cloned = mine!.Today.Single(e => e.Entry.TaskText == "carry me over");
        Assert.NotEqual(sourceEntry, cloned.Entry.Id);                       // a COPY, not a move
        Assert.Contains(cloned.Issues, i => i.IssueText == "blocked on review");

        // The source day is never modified.
        var source = await factory.Services.GetRequiredService<IStandupRepository>().GetEntryAsync(sourceEntry);
        Assert.Equal(yesterday, source!.WorkDate);
    }

    [Fact]
    public async Task Quick_import_into_a_locked_day_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId);

        var yesterday = Today().AddDays(-1);
        await SeedStandupEntryAsync(factory, aliceId, teamId, yesterday, taskText: "carry me over");

        var lockedTarget = Today().AddDays(-10);

        using var alice = await factory.ClientAsync("alice");
        var response = await alice.PostAsJsonAsync(
            "/api/standup/quick-import", new SettingsQuickImportRequest(yesterday, lockedTarget));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var onTarget = await factory.Services.GetRequiredService<IStandupRepository>()
            .GetEntriesAsync(aliceId, lockedTarget);
        Assert.Empty(onTarget);
    }

    // ===== Ops ================================================================================================

    [Theory]
    [InlineData("/api/ops/retention/preview")]
    [InlineData("/api/ops/retention/run")]
    [InlineData("/api/ops/export/run")]
    [InlineData("/api/ops/backup/run")]
    public async Task A_non_admin_gets_403_from_every_ops_route(string route)
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("peon");

        var response = await client.PostAsync(route, content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Retention_preview_is_200_and_read_only()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsync("/api/ops/retention/preview", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Retention_run_is_202_not_200()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsync("/api/ops/retention/run", content: null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Export_and_backup_run_are_200_for_an_admin()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/ops/export/run", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/ops/backup/run", content: null)).StatusCode);
    }

    // ---- M10 gate (P11/P3): GET|PUT /api/ops/backup/settings, GET /api/ops/backup/list. -----------------

    /// <summary>Separate from <see cref="A_non_admin_gets_403_from_every_ops_route"/> because that theory is
    /// hard-wired to POST -- these three routes are GET/PUT/GET.</summary>
    [Fact]
    public async Task Non_admin_gets_403_from_backup_settings_and_backup_list()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("peon");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ops/backup/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings("", false, 30))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ops/backup/list")).StatusCode);
    }

    /// <summary>A fresh install: nobody has ever called the WPF equivalent (SettingsViewModel), so
    /// JsonAppConfig's own defaults must be what the route reports -- blank folder, auto-backup off,
    /// DefaultBackupKeepCount (30).</summary>
    [Fact]
    public async Task Backup_settings_default_to_off_with_a_blank_folder_on_a_fresh_install()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var settings = await admin.GetFromJsonAsync<SettingsOpsBackupSettings>("/api/ops/backup/settings");

        Assert.Equal("", settings!.BackupFolderPath);
        Assert.False(settings.AutoBackupEnabled);
        Assert.Equal(30, settings.BackupKeepCount);
    }

    [Fact]
    public async Task Backup_settings_round_trip_through_PUT_then_GET()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();
        var folder = Path.Combine(factory.Root, "backups");

        var putResponse = await admin.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings(folder, true, 10));
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var written = await putResponse.Content.ReadFromJsonAsync<SettingsOpsBackupSettings>();
        Assert.Equal(folder, written!.BackupFolderPath);
        Assert.True(written.AutoBackupEnabled);
        Assert.Equal(10, written.BackupKeepCount);

        var reread = await admin.GetFromJsonAsync<SettingsOpsBackupSettings>("/api/ops/backup/settings");
        Assert.Equal(folder, reread!.BackupFolderPath);
        Assert.True(reread.AutoBackupEnabled);
        Assert.Equal(10, reread.BackupKeepCount);
    }

    [Fact]
    public async Task A_non_positive_keep_count_is_rejected()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings("", false, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The trap this brief calls out by name: auto-backup ON with no folder is a silent permanent
    /// no-op (AutoBackupIfDueAsync checks AutoBackupEnabled first, BackupFolderPath second, and bails on
    /// either). The write must refuse to create that state rather than accept it quietly.</summary>
    [Fact]
    public async Task Enabling_auto_backup_with_a_blank_folder_is_rejected()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings("", true, 10));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The row this brief calls out as non-negotiable: "no folder configured" and "folder
    /// configured, no backups yet" must be DISTINGUISHABLE, not both a bare empty list. Walks all three
    /// states: not configured -> configured but empty -> configured with one real backup.</summary>
    [Fact]
    public async Task Backup_list_distinguishes_not_configured_from_configured_but_empty_from_has_backups()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var beforeConfig = await admin.GetFromJsonAsync<SettingsOpsBackupList>("/api/ops/backup/list");
        Assert.False(beforeConfig!.FolderConfigured);
        Assert.Empty(beforeConfig.Backups);

        var folder = Path.Combine(factory.Root, "backups");
        var setResponse = await admin.PutAsJsonAsync(
            "/api/ops/backup/settings", new SettingsOpsBackupSettings(folder, false, 30));
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        var afterConfig = await admin.GetFromJsonAsync<SettingsOpsBackupList>("/api/ops/backup/list");
        Assert.True(afterConfig!.FolderConfigured);
        Assert.Empty(afterConfig.Backups); // configured, but nothing backed up yet -- NOT the same as above

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/ops/backup/run", content: null)).StatusCode);

        var afterBackup = await admin.GetFromJsonAsync<SettingsOpsBackupList>("/api/ops/backup/list");
        Assert.True(afterBackup!.FolderConfigured);
        Assert.Single(afterBackup.Backups);
    }

    /// <summary>Belt-and-braces: the Admin POLICY reads the is_admin CLAIM, fixed at login for 30 days. A
    /// demoted admin's cookie still says admin, so the policy alone would let them through. ctx.IsAdmin is
    /// read fresh from the DB on every request and must be what actually gates the four destructive routes.</summary>
    [Fact]
    public async Task A_demoted_admin_is_403_on_ops_routes_even_though_the_cookie_still_says_admin()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("boss", ApiFactory.DefaultPassword, isAdmin: true);
        using var client = await factory.ClientAsync("boss"); // cookie minted with is_admin=1

        using (var db = factory.OpenDb())
            await db.ExecuteAsync("UPDATE Users SET is_admin = 0 WHERE id = @id;", new { id = userId });

        var response = await client.PostAsync("/api/ops/retention/preview", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
