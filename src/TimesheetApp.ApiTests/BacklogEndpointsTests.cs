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

    [Fact]
    public async Task Search_filters_by_term()
    {
        using var factory = new ApiFactory();
        var (client, _, teamId) = await ArrangeAsync(factory);
        await factory.SeedBacklogAsync(teamId, "ALPHA-1");
        await factory.SeedBacklogAsync(teamId, "BETA-1");

        var response = await client.GetAsync("/api/backlogs?term=ALPHA");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<List<BacklogDto>>();
        Assert.Single(results!);
        Assert.Equal("ALPHA-1", results![0].BacklogCode);
    }

    /// <summary>R6 (rule #5): a team id the caller is not a member of, on the query string, must never
    /// leak that team's rows — empty, not 404 (this is a list endpoint, not a single resource).</summary>
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
        var results = await response.Content.ReadFromJsonAsync<List<BacklogDto>>();
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
        Assert.Equal("alice", editor);
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
        Assert.Equal("alice", editor);
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
        Assert.Equal("alice", editor);
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
