using System.Net;
using System.Net.Http.Json;
using Dapper;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>W2-C. Covers <c>/api/backlogs/*</c> and <c>/api/tasks/*</c>, including tag ASSIGNMENT on both
/// (tag CRUD itself is W2-D's, on <c>/api/tags/*</c>).
///
/// <para><b>A note on the "deleted" 409 case (see
/// <see cref="A_write_against_a_hard_deleted_backlog_is_404_not_409_because_authorization_sees_it_first"/>):
/// </b> rule #8 requires every resource id to be team-checked BEFORE the call, via a fresh
/// <c>GetByIdAsync</c>. That is the SAME existence check the checked write's own conflict path would use
/// to decide <c>deleted: true</c> vs <c>false</c>. A row that is gone therefore fails the authorization
/// pre-check (404) before <c>ConcurrencyConflictException(deleted: true)</c> can ever be raised — for
/// Backlogs and Tasks specifically, "409 with deleted:true" is reachable only by a genuine mid-request
/// race (something deleting the row between the pre-check and the write), which no black-box HTTP test can
/// force. The "changed" (`deleted:false`) half of the 409 contract IS fully reachable and is covered
/// below for both entities; the deleted-mapping itself is already proven once, generically, by
/// <c>ConflictContractTests</c> (TimeLogs, which has no id-based pre-check to race).</para></summary>
public sealed class BacklogEndpointsTests
{
    // ==== Backlog CRUD =======================================================================================

    [Fact]
    public async Task A_backlog_can_be_created_with_the_callers_active_team_and_retrieved()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);

        var create = await client.PostAsJsonAsync("/api/backlogs", MinimalCreate("REQ-100", note: "hello"));

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BacklogDto>();
        Assert.Equal("REQ-100", created!.BacklogCode);
        Assert.Equal("hello", created.Note);
        Assert.Equal(teamId, created.TeamId);   // the caller's ACTIVE team, never client-supplied
        Assert.True(created.RowVersion > 0);

        var get = await client.GetAsync($"/api/backlogs/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<BacklogDto>();
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Creating_a_backlog_without_a_code_is_400_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory);

        // NOT an empty-table assumption: TeamBootstrapService seeds a hidden DEFAULT backlog into every
        // fresh database at host startup (repointed to the bootstrap team), so a pristine Backlogs table
        // is never actually empty. Compare before/after instead of asserting an absolute count.
        long CountBacklogs()
        {
            using var db = factory.OpenDb();
            return db.ExecuteScalar<long>("SELECT COUNT(*) FROM Backlogs;");
        }
        var before = CountBacklogs();

        var response = await client.PostAsJsonAsync("/api/backlogs", MinimalCreate("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, CountBacklogs());
    }

    /// <summary>The ORPHAN INSERT. <c>ApiCurrentTeamService.InitializeAsync</c> resolves <c>ActiveTeamId</c>
    /// to the persisted team if it is still a membership, else the first available, ELSE 0 — so a caller in
    /// ZERO teams reaches <c>POST</c> with <c>ActiveTeamId == 0</c>, which the handler maps to
    /// <c>teamId = null</c>, i.e. <c>team_id = NULL</c>.
    ///
    /// <para>Such a row is unreachable the instant it is written: <c>GET /{id}</c> 404s it (the team guard
    /// rejects <c>TeamId is not { } teamId</c> for EVERYONE, admins included), <c>GET /api/backlogs</c> never
    /// returns it (<c>team_id IN (…)</c> cannot match NULL), and no SignalR fires. Before M8.6 the handler
    /// nonetheless answered <c>200 OK</c> with the full DTO: the UI would render the backlog, then lose it
    /// forever on the next refresh. This is the same end state M8.3 closed structurally on <c>UPDATE</c>,
    /// reached instead through <c>INSERT</c>.</para>
    ///
    /// <para><b>A 400 that still wrote the row is exactly the failure this test exists to catch</b> — hence
    /// the before/after count, not just the status code.</para></summary>
    [Fact]
    public async Task Creating_a_backlog_with_no_team_is_refused()
    {
        using var factory = new ApiFactory();

        // Seeded WITHOUT SeedTeamAsync: a user in zero teams. (The fixture's own contract: "A user who is a
        // member of NO team has an empty MemberTeamIds and an ActiveTeamId of 0".)
        await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        var client = await factory.ClientAsync("carol");

        // Assert the PREMISE, don't assume it. TeamBootstrapService joins "every user" to the bootstrap team
        // -- but it runs once at host startup, when carol did not yet exist. If that ever changes, this test
        // would otherwise fail mysteriously on the status code below instead of naming the real cause.
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");
        Assert.Empty(me!.MemberTeamIds);
        Assert.Equal(0, me.ActiveTeamId);

        // The database is bootstrap-seeded with a hidden DEFAULT backlog, so a pristine Backlogs table is
        // never empty — compare before/after rather than asserting zero.
        long CountBacklogs()
        {
            using var db = factory.OpenDb();
            return db.ExecuteScalar<long>("SELECT COUNT(*) FROM Backlogs;");
        }
        var before = CountBacklogs();

        var response = await client.PostAsJsonAsync("/api/backlogs", MinimalCreate("ORPHAN-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, CountBacklogs());

        // And specifically: no teamless row was left behind.
        using var verifyDb = factory.OpenDb();
        Assert.Equal(0, await verifyDb.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Backlogs WHERE team_id IS NULL;"));
    }

    /// <summary>The LIST endpoint returns <see cref="BacklogListItemDto"/>, NOT <c>BacklogDto</c>.
    ///
    /// <para><b>The type argument here is load-bearing, and its being wrong is SILENT.</b> This test read
    /// <c>List&lt;BacklogDto&gt;</c> until M8.6. System.Text.Json binds record constructor parameters BY NAME
    /// and DEFAULTS the ones it cannot find — so it deserialises the list shape into the editor shape without
    /// complaint, quietly filling <c>createdAt</c>/<c>rowVersion</c>/<c>teamId</c> with <c>default</c>. The
    /// test stays green while asserting nothing about the shape actually on the wire. The
    /// <c>TaskCount</c> assertion below is what makes the retype enforceable: it is NON-ZERO, so it cannot
    /// be satisfied by a defaulted field.</para></summary>
    [Fact]
    public async Task Search_filters_by_term()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var alphaId = await factory.SeedBacklogAsync(teamId, "ALPHA-1");
        await factory.SeedBacklogAsync(teamId, "BETA-1");
        await factory.SeedTaskAsync(alphaId, "Some work");

        var response = await client.GetAsync("/api/backlogs?term=ALPHA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<BacklogListItemDto>>();
        Assert.Single(results!);
        Assert.Equal("ALPHA-1", results![0].BacklogCode);
        Assert.Equal(1, results[0].TaskCount);
    }

    /// <summary>The TASKS column. Counts ACTIVE tasks only, per backlog — a soft-deleted task must drop out,
    /// and a backlog with no tasks at all must report 0 rather than blowing up on a dictionary miss.</summary>
    [Fact]
    public async Task Backlog_list_carries_the_active_task_count()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "COUNT-1");
        await factory.SeedTaskAsync(backlogId, "One");
        await factory.SeedTaskAsync(backlogId, "Two");
        var doomedTaskId = await factory.SeedTaskAsync(backlogId, "Three");

        // A second backlog with NO tasks: proves the count is grouped PER BACKLOG (not one number applied to
        // every row) and that the dictionary MISS maps to 0.
        await factory.SeedBacklogAsync(teamId, "COUNT-2");

        var deactivate = await client.PutAsJsonAsync(
            $"/api/tasks/{doomedTaskId}/active", new TaskActiveRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var response = await client.GetAsync("/api/backlogs?term=COUNT-");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<BacklogListItemDto>>();
        Assert.Equal(2, results!.Single(b => b.BacklogCode == "COUNT-1").TaskCount);
        Assert.Equal(0, results.Single(b => b.BacklogCode == "COUNT-2").TaskCount);
    }

    /// <summary>R6 (rule #5): a team id the caller is not a member of, on the query string, must never
    /// leak that team's rows — empty, not 404 (this is a list endpoint, not a single resource).
    ///
    /// <para>Retyped to <see cref="BacklogListItemDto"/> in M8.6 for the reason on
    /// <see cref="Search_filters_by_term"/>. There is no <c>TaskCount</c> to assert here — the list being
    /// EMPTY is the whole subject of the test. It does, however, now exercise the batched task lookup with
    /// an EMPTY id list, which is the one input that would throw on a naive <c>IN ()</c>.</para></summary>
    [Fact]
    public async Task A_team_id_the_caller_is_not_a_member_of_on_the_query_string_returns_empty_never_rows()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        await factory.SeedBacklogAsync(otherTeamId, "SECRET-1");

        var response = await client.GetAsync($"/api/backlogs?teamIds={otherTeamId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<BacklogListItemDto>>();
        Assert.Empty(results!);
    }

    [Fact]
    public async Task A_backlog_update_is_checked_and_returns_the_new_row_version()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-200");
        var current = await GetBacklogAsync(client, backlogId);

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(current, note: "edited"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > current.RowVersion);

        var refetched = await GetBacklogAsync(client, backlogId);
        Assert.Equal("edited", refetched.Note);
    }

    /// <summary>Rule #4 — the biggest silent trap in this file. <c>UpdateCheckedAsync</c> takes
    /// <c>changedByUserId</c>/<c>changedByName</c> as OPTIONAL params: omit them and it still compiles and
    /// the write still succeeds, just anonymously.</summary>
    [Fact]
    public async Task A_backlog_update_writes_an_audit_row_naming_the_editor()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory, "alice");
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-201");
        var current = await GetBacklogAsync(client, backlogId);

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(current, note: "changed by alice"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var db = factory.OpenDb();
        var editor = await db.ExecuteScalarAsync<string?>(
            "SELECT changed_by_name FROM BacklogAudit WHERE backlog_id = @id AND field = 'note' ORDER BY id DESC LIMIT 1;",
            new { id = backlogId });
        // The DISPLAY name ("Alice Nguyen"), not the login username ("alice") — IClientContext.UserName is
        // "the changedByName on every audited write". Asserting the literal "alice" here proved NOTHING
        // until W2.5: the fixture wrote both columns as the same string, so an endpoint that had audited
        // the username by mistake passed this assertion too.
        Assert.Equal(ApiFactory.DisplayNameFor("alice"), editor);
    }

    [Fact]
    public async Task A_stale_backlog_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-202");
        var v1 = await GetBacklogAsync(client, backlogId);

        // A competing write, arranged directly against the API's own database (ApiFactory.OpenDb),
        // simulating a second client that already saved.
        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Backlogs SET row_version = row_version + 1 WHERE id = @id;", new { id = backlogId });
        }

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(v1, note: "my stale edit"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Backlogs", body!.Table);
        Assert.Equal(backlogId, body.Id);
        Assert.False(body.Deleted);
    }

    /// <summary>See the class-level doc. <c>IBacklogRepository</c> has no delete at all, so this row can
    /// never vanish through the app — the raw DELETE below only exists to observe the mapping. The
    /// authorization pre-check (rule #8) re-reads the row by id BEFORE the checked write, exactly the
    /// existence check the checked write's own conflict path would otherwise use to report
    /// <c>deleted:true</c>. A gone row therefore fails authorization first: 404, not 409.</summary>
    [Fact]
    public async Task A_write_against_a_hard_deleted_backlog_is_404_not_409_because_authorization_sees_it_first()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-203");
        var v1 = await GetBacklogAsync(client, backlogId);

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync("DELETE FROM Backlogs WHERE id = @id;", new { id = backlogId });
        }

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(v1, note: "edit of a gone row"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Rule #8's headline danger. <c>UpdateCheckedAsync(Backlog, …)</c> writes ALL 15 columns —
    /// including <c>team_id</c> — so a DTO that merely omits <c>teamId</c> maps to <c>TeamId = null</c>
    /// unless the endpoint re-reads and merges. <c>BacklogUpdateRequest</c> structurally has no
    /// <c>TeamId</c> property at all; this proves the endpoint still preserves it.</summary>
    [Fact]
    public async Task A_backlog_update_omitting_team_id_leaves_team_id_unchanged_not_null()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-204");
        var current = await GetBacklogAsync(client, backlogId);

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(current, note: "still on my team"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var db = factory.OpenDb();
        var storedTeamId = await db.ExecuteScalarAsync<long?>(
            "SELECT team_id FROM Backlogs WHERE id = @id;", new { id = backlogId });
        Assert.Equal((long)teamId, storedTeamId);
    }

    [Fact]
    public async Task Editing_another_teams_backlog_by_id_is_404_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-2");

        // Built without ever legitimately reading the row -- an attacker would not have a real DTO either.
        var attempt = new BacklogUpdateRequest(
            "HIJACKED", "ARCS", null, null, null, null, null, null, null, null, null, null, "pwned", null, 0);

        var response = await client.PutAsJsonAsync($"/api/backlogs/{otherBacklogId}", attempt);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var db = factory.OpenDb();
        var code = await db.ExecuteScalarAsync<string>(
            "SELECT backlog_code FROM Backlogs WHERE id = @id;", new { id = otherBacklogId });
        Assert.Equal("SECRET-2", code);
    }

    [Fact]
    public async Task Backlog_tags_can_be_assigned_read_back_and_are_version_checked()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-205");
        var tagId = await SeedTagAsync(factory, "Hot");
        var current = await GetBacklogAsync(client, backlogId);

        var response = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}/tags", new BacklogTagsRequest(new[] { tagId }, current.RowVersion));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > current.RowVersion);

        var tagsResponse = await client.GetAsync($"/api/backlogs/{backlogId}/tags");
        var tagIds = await tagsResponse.Content.ReadFromJsonAsync<List<int>>();
        Assert.Equal(new[] { tagId }, tagIds);

        // Tags ride the PARENT backlog's row_version (BacklogTags carries none of its own) -- the now-stale
        // `current.RowVersion` must still 409 on a second attempt.
        var stale = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}/tags", new BacklogTagsRequest(Array.Empty<int>(), current.RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    /// <summary>The editor's change-history panel. <c>IBacklogRepository.GetAuditAsync</c> has existed since
    /// v2 but was never exposed over HTTP, so the panel had nothing to read and would have rendered an empty
    /// box forever.
    ///
    /// <para>Asserts the DISPLAY name ("Alice Nguyen"), not the login username ("alice") — the audit column
    /// is <c>changed_by_name</c> and the repository audits by NAME precisely so a deleted user's history
    /// still reads. The fixture makes the two strings genuinely different, so this distinguishes them.</para></summary>
    [Fact]
    public async Task Backlog_audit_returns_the_change_history()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory, "alice");
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-400");
        var current = await GetBacklogAsync(client, backlogId);

        var update = await client.PutAsJsonAsync(
            $"/api/backlogs/{backlogId}", ToUpdateRequest(current, note: "audited note"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var response = await client.GetAsync($"/api/backlogs/{backlogId}/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await response.Content.ReadFromJsonAsync<List<BacklogAuditDto>>();
        var noteChange = Assert.Single(entries!, e => e.Field == "note");
        Assert.Equal(ApiFactory.DisplayNameFor("alice"), noteChange.ChangedByName);
        // Pins the OldValue/NewValue ORDER: a swapped projection is otherwise invisible.
        Assert.Equal("audited note", noteChange.NewValue);
    }

    /// <summary>The audit route carries the same team guard as every other single-backlog route, so another
    /// team's history is 404 — not 403, and certainly not readable.</summary>
    [Fact]
    public async Task Backlog_audit_for_another_teams_backlog_is_404()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-6");

        var response = await client.GetAsync($"/api/backlogs/{otherBacklogId}/audit");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Rule #1: "continue to next month" MUST go through <c>IBacklogContinuationService</c> — it
    /// copies tags, copies not-Done tasks and writes the 'continued' audit row, none of which a raw INSERT
    /// would do.</summary>
    [Fact]
    public async Task Continuing_a_backlog_to_next_month_goes_through_the_service_and_copies_tags_and_open_tasks()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-206");
        await factory.SeedTaskAsync(backlogId, "Still open");
        var tagId = await SeedTagAsync(factory, "Carried");
        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@b, @t);", new { b = backlogId, t = tagId });
        }

        var response = await client.PostAsJsonAsync(
            $"/api/backlogs/{backlogId}/continue", new BacklogContinueRequest("2026-09"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<BacklogDto>();
        Assert.NotEqual(backlogId, created!.Id);
        Assert.Equal("2026-09", created.PeriodMonth);
        Assert.Equal("Continue", created.Type);

        using var verifyDb = factory.OpenDb();
        Assert.Equal(1, await verifyDb.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM BacklogTags WHERE backlog_id = @id AND tag_id = @tag;",
            new { id = created.Id, tag = tagId }));
        Assert.Equal(1, await verifyDb.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Tasks WHERE backlog_id = @id;", new { id = created.Id }));
        Assert.Equal("continued", await verifyDb.ExecuteScalarAsync<string?>(
            "SELECT field FROM BacklogAudit WHERE backlog_id = @id AND field = 'continued';",
            new { id = created.Id }));
    }

    [Fact]
    public async Task Continuing_to_a_period_that_already_has_the_code_is_400()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-207");

        var first = await client.PostAsJsonAsync(
            $"/api/backlogs/{backlogId}/continue", new BacklogContinueRequest("2026-10"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/backlogs/{backlogId}/continue", new BacklogContinueRequest("2026-10"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    // ==== Task CRUD ==========================================================================================

    /// <summary>Verifies an assumption rather than trusting it: a non-nullable <c>[FromQuery] int</c> is
    /// required by minimal-API convention, so an entirely missing <c>backlogId</c> should fail binding
    /// with 400 before the handler body (and the authorization check) ever runs.</summary>
    [Fact]
    public async Task Listing_tasks_without_a_backlog_id_is_400()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory);

        var response = await client.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_task_can_be_created_and_retrieved()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-300");

        var create = await client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(backlogId, "New task", 0));

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<TaskItemDto>();
        Assert.Equal("New task", created!.TaskName);
        Assert.Equal("Todo", created.Status);
        Assert.True(created.IsActive);

        var list = await client.GetAsync($"/api/tasks?backlogId={backlogId}");
        var tasks = await list.Content.ReadFromJsonAsync<List<TaskItemDto>>();
        Assert.Contains(tasks!, t => t.Id == created.Id);
    }

    [Fact]
    public async Task Creating_a_task_without_a_name_is_400()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-301");

        var response = await client.PostAsJsonAsync("/api/tasks", new TaskCreateRequest(backlogId, "   ", 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_task_under_another_teams_backlog_is_404_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-3");

        var response = await client.PostAsJsonAsync(
            "/api/tasks", new TaskCreateRequest(otherBacklogId, "Sneaky task", 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var db = factory.OpenDb();
        Assert.Equal(0, await db.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Tasks WHERE backlog_id = @id;", new { id = otherBacklogId }));
    }

    [Fact]
    public async Task Listing_tasks_for_another_teams_backlog_is_404()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-4");

        var response = await client.GetAsync($"/api/tasks?backlogId={otherBacklogId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_task_update_is_checked_and_returns_the_new_row_version()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-302");
        var taskId = await factory.SeedTaskAsync(backlogId, "Rename me");
        var current = await GetTaskAsync(client, taskId);

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}", new TaskUpdateRequest("Renamed", 2, "In-process", current.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > current.RowVersion);

        var refetched = await GetTaskAsync(client, taskId);
        Assert.Equal("Renamed", refetched.TaskName);
        Assert.Equal(2, refetched.OrderIndex);
        Assert.Equal("In-process", refetched.Status);
    }

    [Fact]
    public async Task A_stale_task_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-303");
        var taskId = await factory.SeedTaskAsync(backlogId, "Conflict me");
        var v1 = await GetTaskAsync(client, taskId);

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Tasks SET row_version = row_version + 1 WHERE id = @id;", new { id = taskId });
        }

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}", new TaskUpdateRequest("Stale rename", v1.OrderIndex, v1.Status, v1.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Tasks", body!.Table);
        Assert.False(body.Deleted);
    }

    [Fact]
    public async Task Task_status_can_be_updated_and_is_audited()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory, "alice");
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-304");
        var taskId = await factory.SeedTaskAsync(backlogId, "Status me");
        var current = await GetTaskAsync(client, taskId);

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/status", new TaskStatusRequest("Done", current.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = factory.OpenDb();
        var editor = await db.ExecuteScalarAsync<string?>(
            "SELECT changed_by_name FROM TaskAudit WHERE task_id = @id AND field = 'status' ORDER BY id DESC LIMIT 1;",
            new { id = taskId });
        // The DISPLAY name ("Alice Nguyen"), not the login username ("alice") — IClientContext.UserName is
        // "the changedByName on every audited write". Asserting the literal "alice" here proved NOTHING
        // until W2.5: the fixture wrote both columns as the same string, so an endpoint that had audited
        // the username by mistake passed this assertion too.
        Assert.Equal(ApiFactory.DisplayNameFor("alice"), editor);
    }

    /// <summary>M9 P2.5. <c>PUT /api/tasks/{id}/status</c> now DECLARES a 400 (the Task List's inline status
    /// dropdown needs the generated client to see it), and nothing exercised that guard — the route's only
    /// test was the happy path above. A declared status that no test reaches is an unverified claim, and an
    /// unverified claim in an OpenAPI document is exactly the kind of typed lie this milestone is closing.</summary>
    [Fact]
    public async Task Updating_a_task_status_to_an_empty_string_is_400_and_leaves_the_status_unchanged()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-309");
        var taskId = await factory.SeedTaskAsync(backlogId, "Blank status me");
        var current = await GetTaskAsync(client, taskId);

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/status", new TaskStatusRequest("   ", current.RowVersion));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A 400 that still wrote the row is the failure worth catching -- assert the status did not move.
        var refetched = await GetTaskAsync(client, taskId);
        Assert.Equal(current.Status, refetched.Status);
    }

    /// <summary>M9 P2.5. Proves the 409 the route now declares. The status write is version-CHECKED
    /// (<c>UpdateStatusCheckedAsync</c>), so the inline dropdown must be able to tell a lost update from a
    /// server error — and it can only do that if the generated client knows 409 is a possible outcome.</summary>
    [Fact]
    public async Task A_stale_task_status_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-310");
        var taskId = await factory.SeedTaskAsync(backlogId, "Status conflict me");
        var v1 = await GetTaskAsync(client, taskId);

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Tasks SET row_version = row_version + 1 WHERE id = @id;", new { id = taskId });
        }

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/status", new TaskStatusRequest("Done", v1.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Tasks", body!.Table);
        Assert.False(body.Deleted);
    }

    [Fact]
    public async Task Task_extended_fields_can_be_updated_and_are_audited()
    {
        using var factory = new ApiFactory();
        var (client, userId, teamId) = await ArrangeAsync(factory, "alice");
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-305");
        var taskId = await factory.SeedTaskAsync(backlogId, "Extend me");
        var current = await GetTaskAsync(client, taskId);

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/extended", new TaskExtendedRequest("Investigate", userId, current.RowVersion));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<SavedBody>();
        Assert.True(saved!.RowVersion > current.RowVersion);

        using var db = factory.OpenDb();
        var editor = await db.ExecuteScalarAsync<string?>(
            "SELECT changed_by_name FROM TaskAudit WHERE task_id = @id AND field = 'type' ORDER BY id DESC LIMIT 1;",
            new { id = taskId });
        // The DISPLAY name ("Alice Nguyen"), not the login username ("alice") — IClientContext.UserName is
        // "the changedByName on every audited write". Asserting the literal "alice" here proved NOTHING
        // until W2.5: the fixture wrote both columns as the same string, so an endpoint that had audited
        // the username by mistake passed this assertion too.
        Assert.Equal(ApiFactory.DisplayNameFor("alice"), editor);
    }

    /// <summary>M9 P2.5. Proves the 409 <c>PUT /api/tasks/{id}/extended</c> now declares — the Task List's
    /// inline type/assignee editors are version-checked exactly like the status dropdown.</summary>
    [Fact]
    public async Task A_stale_task_extended_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var (client, userId, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-311");
        var taskId = await factory.SeedTaskAsync(backlogId, "Extended conflict me");
        var v1 = await GetTaskAsync(client, taskId);

        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Tasks SET row_version = row_version + 1 WHERE id = @id;", new { id = taskId });
        }

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/extended", new TaskExtendedRequest("Investigate", userId, v1.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Tasks", body!.Table);
        Assert.False(body.Deleted);
    }

    [Fact]
    public async Task Task_tags_can_be_assigned_and_read_back()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-306");
        var taskId = await factory.SeedTaskAsync(backlogId, "Tag me");
        var tagId = await SeedTagAsync(factory, "Blocked");
        var current = await GetTaskAsync(client, taskId);

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/tags", new TaskTagsRequest(new[] { tagId }, current.RowVersion));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tagsResponse = await client.GetAsync($"/api/tasks/{taskId}/tags");
        var tagIds = await tagsResponse.Content.ReadFromJsonAsync<List<int>>();
        Assert.Equal(new[] { tagId }, tagIds);
    }

    /// <summary>M9 P2.5. Proves the 409 <c>PUT /api/tasks/{id}/tags</c> now declares. Task tags ride the
    /// PARENT task's <c>row_version</c> (TaskTags carries none of its own — the same arrangement as
    /// BacklogTags, asserted for backlogs in
    /// <see cref="Backlog_tags_can_be_assigned_read_back_and_are_version_checked"/>), so two people re-ticking
    /// the chips on one task clobber each other exactly like any other inline edit.</summary>
    [Fact]
    public async Task A_stale_task_tags_update_is_409_with_deleted_false()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-312");
        var taskId = await factory.SeedTaskAsync(backlogId, "Tag conflict me");
        var tagId = await SeedTagAsync(factory, "Contended");
        var current = await GetTaskAsync(client, taskId);

        var first = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/tags", new TaskTagsRequest(new[] { tagId }, current.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // `current.RowVersion` is now stale -- the write above bumped the PARENT task's version.
        var stale = await client.PutAsJsonAsync(
            $"/api/tasks/{taskId}/tags", new TaskTagsRequest(Array.Empty<int>(), current.RowVersion));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Equal("Tasks", body!.Table);
        Assert.False(body.Deleted);

        // The rejected write must not have cleared the tags.
        var tagsResponse = await client.GetAsync($"/api/tasks/{taskId}/tags");
        Assert.Equal(new[] { tagId }, await tagsResponse.Content.ReadFromJsonAsync<List<int>>());
    }

    /// <summary>Rule #9: bump-only BY DESIGN, no checked sibling — a checked reorder would 409-storm an
    /// ordinary drag (<c>SetOrderAsync</c> runs once per row over the whole list).</summary>
    [Fact]
    public async Task A_task_can_be_reordered_bump_only_and_a_stale_row_version_elsewhere_never_conflicts()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-307");
        var taskId = await factory.SeedTaskAsync(backlogId, "Reorder me");

        var first = await client.PutAsJsonAsync($"/api/tasks/{taskId}/order", new TaskOrderRequest(3));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // A concurrent write elsewhere bumps row_version -- bump-only must not care.
        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Tasks SET row_version = row_version + 1 WHERE id = @id;", new { id = taskId });
        }

        var second = await client.PutAsJsonAsync($"/api/tasks/{taskId}/order", new TaskOrderRequest(5));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var verifyDb = factory.OpenDb();
        Assert.Equal(5, await verifyDb.ExecuteScalarAsync<long>(
            "SELECT order_index FROM Tasks WHERE id = @id;", new { id = taskId }));
    }

    [Fact]
    public async Task A_task_can_be_soft_deleted_and_restored_bump_only()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-308");
        var taskId = await factory.SeedTaskAsync(backlogId, "Deletable");

        var deactivate = await client.PutAsJsonAsync($"/api/tasks/{taskId}/active", new TaskActiveRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        var afterDeactivate = await client.GetAsync($"/api/tasks?backlogId={backlogId}");
        var listAfterDeactivate = await afterDeactivate.Content.ReadFromJsonAsync<List<TaskItemDto>>();
        Assert.DoesNotContain(listAfterDeactivate!, t => t.Id == taskId);

        var restore = await client.PutAsJsonAsync($"/api/tasks/{taskId}/active", new TaskActiveRequest(true));
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        var afterRestore = await client.GetAsync($"/api/tasks?backlogId={backlogId}");
        var listAfterRestore = await afterRestore.Content.ReadFromJsonAsync<List<TaskItemDto>>();
        Assert.Contains(listAfterRestore!, t => t.Id == taskId);
    }

    [Fact]
    public async Task Editing_another_teams_task_by_id_is_404_and_writes_nothing()
    {
        using var factory = new ApiFactory();
        var (client, _, _) = await ArrangeAsync(factory, "alice");

        var outsiderId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var otherTeamId = await factory.SeedTeamAsync("Team B", outsiderId);
        var otherBacklogId = await factory.SeedBacklogAsync(otherTeamId, "SECRET-5");
        var otherTaskId = await factory.SeedTaskAsync(otherBacklogId, "Not yours");

        var response = await client.PutAsJsonAsync(
            $"/api/tasks/{otherTaskId}", new TaskUpdateRequest("Hijacked", 0, "Done", 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var db = factory.OpenDb();
        var name = await db.ExecuteScalarAsync<string>(
            "SELECT task_name FROM Tasks WHERE id = @id;", new { id = otherTaskId });
        Assert.Equal("Not yours", name);
    }

    // ==== helpers ============================================================================================

    private static async Task<(HttpClient Client, int UserId, int TeamId)> ArrangeAsync(
        ApiFactory factory, string userName = "alice")
    {
        var userId = await factory.SeedUserAsync(userName, ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", userId);
        var client = await factory.ClientAsync(userName);
        return (client, userId, teamId);
    }

    private static async Task<int> SeedTagAsync(ApiFactory factory, string text)
    {
        using var db = factory.OpenDb();
        return await db.ExecuteScalarAsync<int>(
            @"INSERT INTO Tags(text, icon, color, created_at) VALUES(@text, 'flag', '#ff0000', @now);
              SELECT last_insert_rowid();",
            new { text, now = "2026-07-01T00:00:00Z" });
    }

    private static async Task<BacklogDto> GetBacklogAsync(HttpClient client, int id) =>
        (await (await client.GetAsync($"/api/backlogs/{id}")).Content.ReadFromJsonAsync<BacklogDto>())!;

    private static async Task<TaskItemDto> GetTaskAsync(HttpClient client, int id) =>
        (await (await client.GetAsync($"/api/tasks/{id}")).Content.ReadFromJsonAsync<TaskItemDto>())!;

    private static BacklogCreateRequest MinimalCreate(string code, string project = "ARCS", string? note = null) =>
        new(code, project, null, null, null, null, null, null, null, null, null, null, note, null);

    /// <summary>Builds an update request FROM a previously-read DTO, exactly like a real client would:
    /// read, tweak one field, PUT back the rest unchanged. TeamId is deliberately never part of this
    /// shape (rule #8) -- there is no parameter for it here at all.</summary>
    private static BacklogUpdateRequest ToUpdateRequest(BacklogDto b, string? note = null) =>
        new(b.BacklogCode, b.Project, b.StartDate, b.EndDate, b.PeriodMonth, b.Type,
            b.AssigneeUserId, b.DeadlineInternal, b.DeadlineExternal, b.RoughEstimateHours,
            b.OfficialEstimateHours, b.ProgressPercent, note ?? b.Note, b.PcaContactId,
            b.RowVersion);
}
