using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
        string section = StandupSection.Today, string taskText = "Do the thing", string status = "Todo") =>
        await factory.Services.GetRequiredService<IStandupRepository>().InsertEntryAsync(new StandupEntry(
            0, userId, workDate, section, null, "ADHOC-1", taskText, "", null, status, 0,
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

    [Fact]
    public async Task Users_all_list_is_admin_only_but_active_list_is_open()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var alice = await factory.ClientAsync("alice");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/users/all")).StatusCode);
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
