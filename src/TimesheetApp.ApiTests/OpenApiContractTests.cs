using System.Net;
using System.Text.Json;
using Xunit;                       // NOT covered by ImplicitUsings -- every test file here has it explicitly

namespace TimesheetApp.ApiTests;

/// <summary>
/// The generated TypeScript client can only contain a route whose C# DECLARES a response type. A route
/// that returns IResult via Results.Ok(x) and says nothing else is described by OpenAPI as an empty 200,
/// and codegen then emits a method typed `void` for an endpoint that returns data. That failure is
/// SILENT -- codegen SUCCEEDS. This test is what makes the rule enforceable.
/// </summary>
public sealed class OpenApiContractTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private JsonElement _paths;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();
        var res = await _factory.AnonymousClient().GetAsync("/swagger/v1/swagger.json");  // deliberately anonymous
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        _paths = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                             .RootElement.GetProperty("paths").Clone();
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    // NOTE on "/api/backlogs/{id}/audit": the ROUTE is declared "{id:int}", but ApiExplorer STRIPS the route
    // constraint from the OpenAPI path. The key here is therefore "{id}" -- "{id:int}" is not in the document
    // and lookups for it fail with a KeyNotFoundException that reads exactly like a missing route.
    [Theory]
    [InlineData("/api/backlogs", "get", "200")]
    [InlineData("/api/backlogs", "post", "200")]
    [InlineData("/api/backlogs/{id}", "get", "200")]
    [InlineData("/api/backlogs/{id}", "put", "200")]
    [InlineData("/api/backlogs/{id}/audit", "get", "200")]
    [InlineData("/api/tasks", "get", "200")]
    [InlineData("/api/tasks", "post", "200")]
    // M8.5 shipped POST /api/tasks declaring only 200 and 404 while the handler also returns
    // 400 + ValidationBody for an empty TaskName. Nothing asserted the 400, so the generated client could
    // not see it and treated a rejected create as an unexpected error. This row is what makes it stay fixed.
    [InlineData("/api/tasks", "post", "400")]
    [InlineData("/api/tasks/{id}", "put", "200")]
    [InlineData("/api/tasks/{id}", "put", "400")]
    [InlineData("/api/tasks/{id}", "put", "409")]
    [InlineData("/api/users", "get", "200")]
    [InlineData("/api/users/names", "get", "200")]
    [InlineData("/api/pca-contacts", "get", "200")]
    [InlineData("/api/pca-contacts/names", "get", "200")]
    // ---- M9 P2: SettingsEndpoints. Users · Tags · Teams · PcaContacts · Templates · Holidays ·
    // DefaultTasks · Standup · Ops · Me. Every one of these was an UNANNOTATED Results.Ok(x) returning
    // IResult -- described by OpenAPI as an empty 200 and omitted from the generated client entirely.
    [InlineData("/api/me/active-team", "put", "400")]
    [InlineData("/api/tags", "get", "200")]
    [InlineData("/api/tags", "post", "200")]
    [InlineData("/api/tags", "post", "400")]
    [InlineData("/api/tags/{id}", "put", "200")]
    [InlineData("/api/tags/{id}", "put", "400")]
    [InlineData("/api/tags/{id}", "put", "409")]
    [InlineData("/api/teams", "get", "200")]
    [InlineData("/api/teams", "post", "200")]
    [InlineData("/api/teams", "post", "400")]
    [InlineData("/api/teams/all", "get", "200")]
    [InlineData("/api/teams/{id}", "put", "200")]
    [InlineData("/api/teams/{id}", "put", "400")]
    [InlineData("/api/teams/{id}", "put", "409")]
    // A BARE ARRAY OF INTS (GetUserIdsForTeamAsync -> IReadOnlyList<int>), NOT a list of UserDto. A wrong
    // .Produces<T>() here would generate a TYPED LIE, which is worse than no method at all.
    [InlineData("/api/teams/{id}/members", "get", "200")]
    [InlineData("/api/teams/{id}/members", "put", "200")]
    [InlineData("/api/teams/{id}/members", "put", "409")]
    [InlineData("/api/pca-contacts/all", "get", "200")]
    [InlineData("/api/pca-contacts", "post", "200")]
    [InlineData("/api/pca-contacts", "post", "400")]
    [InlineData("/api/pca-contacts/{id}", "put", "200")]
    [InlineData("/api/pca-contacts/{id}", "put", "400")]
    [InlineData("/api/pca-contacts/{id}", "put", "409")]
    [InlineData("/api/users/all", "get", "200")]
    [InlineData("/api/users", "post", "200")]
    [InlineData("/api/users", "post", "400")]
    [InlineData("/api/users/{id}", "put", "200")]
    [InlineData("/api/users/{id}", "put", "400")]
    [InlineData("/api/users/{id}", "put", "409")]
    [InlineData("/api/users/{id}/username", "put", "200")]
    [InlineData("/api/users/{id}/username", "put", "400")]
    [InlineData("/api/users/{id}/username", "put", "409")]
    [InlineData("/api/templates", "get", "200")]
    [InlineData("/api/templates", "post", "200")]
    [InlineData("/api/templates", "post", "400")]
    [InlineData("/api/templates", "delete", "400")]
    [InlineData("/api/holidays", "get", "200")]
    [InlineData("/api/holidays/{date}", "delete", "400")]
    [InlineData("/api/default-tasks", "get", "200")]
    [InlineData("/api/default-tasks", "post", "200")]
    [InlineData("/api/default-tasks", "post", "400")]
    [InlineData("/api/standup/entries", "get", "200")]
    [InlineData("/api/standup/entries", "get", "400")]
    [InlineData("/api/standup/board", "get", "200")]
    [InlineData("/api/standup/board", "get", "400")]
    // These three return a BARE int (a new id, and for quick-import a COUNT) -- not a DTO. Pinned so nobody
    // "helpfully" invents a StandupEntryCreateResponse the server does not send.
    [InlineData("/api/standup/entries", "post", "200")]
    [InlineData("/api/standup/entries", "post", "400")]
    [InlineData("/api/standup/quick-import", "post", "200")]
    [InlineData("/api/standup/quick-import", "post", "400")]
    [InlineData("/api/standup/entries/{entryId}/issues", "post", "200")]
    [InlineData("/api/standup/entries/{entryId}/issues", "post", "400")]
    [InlineData("/api/standup/entries/{entryId}", "put", "400")]
    [InlineData("/api/standup/entries/{entryId}", "delete", "400")]
    [InlineData("/api/standup/entries/reorder", "put", "400")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "put", "200")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "put", "409")]
    // RetentionPreview is a CORE type (TimesheetApp.Services), passed straight through by the handler.
    [InlineData("/api/ops/retention/preview", "post", "200")]
    [InlineData("/api/ops/export/run", "post", "200")]
    [InlineData("/api/ops/backup/run", "post", "200")]
    public void Route_declares_a_response_SCHEMA_not_just_a_status(string path, string verb, string status)
    {
        var response = _paths.GetProperty(path).GetProperty(verb)
                             .GetProperty("responses").GetProperty(status);

        // `description: "OK"` with no `content` IS the silent failure. Assert the schema exists.
        Assert.True(response.TryGetProperty("content", out var content),
            $"{verb.ToUpperInvariant()} {path} declares {status} with NO response body. " +
            "Codegen will emit a method typed `void` for an endpoint that returns data.");
        Assert.True(content.GetProperty("application/json").TryGetProperty("schema", out _));
    }

    [Theory]
    [InlineData("/api/tasks/{id}/active", "put")]
    [InlineData("/api/tasks/{id}/order", "put")]
    public void Bump_only_route_declares_204(string path, string verb)
    {
        var responses = _paths.GetProperty(path).GetProperty(verb).GetProperty("responses");
        Assert.True(responses.TryGetProperty("204", out _),
            $"{verb.ToUpperInvariant()} {path} must declare 204 -- it returns Results.NoContent().");
    }

    /// <summary>M9 P2. The body-less half of SettingsEndpoints: routes whose success is a STATUS, not a
    /// payload. Deliberately a separate theory from <see cref="Bump_only_route_declares_204"/> rather than
    /// more rows on it — most of these are not "bump-only" writes (several are DELETEs), and one is not even
    /// a 204.
    ///
    /// <para>The status is a PARAMETER because <c>POST /api/ops/retention/run</c> is <b>202 Accepted</b>, not
    /// 204 and not 200: <c>RetentionService</c> holds one <c>BEGIN IMMEDIATE</c> across six bulk DELETEs, so
    /// it runs on a background task and the response cannot carry a result. A client generated against a 200
    /// would sit and wait for a body that is never coming.</para></summary>
    [Theory]
    [InlineData("/api/me/active-team", "put", "204")]
    [InlineData("/api/tags/{id}", "delete", "204")]
    [InlineData("/api/teams/{id}/active", "put", "204")]
    [InlineData("/api/pca-contacts/{id}/active", "put", "204")]
    [InlineData("/api/users/{id}/active", "put", "204")]
    [InlineData("/api/templates/{id}", "delete", "204")]
    [InlineData("/api/templates", "delete", "204")]
    [InlineData("/api/holidays", "post", "204")]
    [InlineData("/api/holidays/{date}", "delete", "204")]
    [InlineData("/api/default-tasks/{id}/active", "put", "204")]
    [InlineData("/api/default-tasks/sync", "post", "204")]
    [InlineData("/api/standup/entries/{entryId}", "put", "204")]
    [InlineData("/api/standup/entries/{entryId}", "delete", "204")]
    [InlineData("/api/standup/entries/reorder", "put", "204")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "delete", "204")]
    [InlineData("/api/ops/retention/run", "post", "202")]
    public void Route_declares_the_body_less_status_it_actually_returns(string path, string verb, string status)
    {
        var responses = _paths.GetProperty(path).GetProperty(verb).GetProperty("responses");
        Assert.True(responses.TryGetProperty(status, out _),
            $"{verb.ToUpperInvariant()} {path} must declare {status} -- that is the status the handler returns.");
    }

    [Theory]
    [InlineData("/api/backlogs", "get", "Backlogs")]
    [InlineData("/api/backlogs", "post", "Backlogs")]
    [InlineData("/api/backlogs/{id}", "get", "Backlogs")]
    [InlineData("/api/backlogs/{id}", "put", "Backlogs")]
    [InlineData("/api/backlogs/{id}/audit", "get", "Backlogs")]
    [InlineData("/api/tasks", "get", "Tasks")]
    [InlineData("/api/tasks", "post", "Tasks")]
    [InlineData("/api/tasks/{id}", "put", "Tasks")]
    [InlineData("/api/tasks/{id}/active", "put", "Tasks")]
    [InlineData("/api/tasks/{id}/order", "put", "Tasks")]
    [InlineData("/api/users", "get", "Users")]
    [InlineData("/api/users/names", "get", "Users")]
    [InlineData("/api/pca-contacts", "get", "PcaContacts")]
    [InlineData("/api/pca-contacts/names", "get", "PcaContacts")]
    // ---- M9 P2: SettingsEndpoints. An UNTAGGED minimal-API route defaults to the ASSEMBLY name
    // ("TimesheetApp.Api") as its tag, and ng-openapi-gen selects operations BY TAG -- so before M9 every one
    // of these was omitted from the generated client entirely. The tag is what makes the route EXIST to the
    // front end.
    [InlineData("/api/me/active-team", "put", "Me")]
    [InlineData("/api/tags", "get", "Tags")]
    [InlineData("/api/tags", "post", "Tags")]
    [InlineData("/api/tags/{id}", "put", "Tags")]
    [InlineData("/api/tags/{id}", "delete", "Tags")]
    [InlineData("/api/teams", "get", "Teams")]
    [InlineData("/api/teams", "post", "Teams")]
    [InlineData("/api/teams/all", "get", "Teams")]
    [InlineData("/api/teams/{id}", "put", "Teams")]
    [InlineData("/api/teams/{id}/active", "put", "Teams")]
    [InlineData("/api/teams/{id}/members", "get", "Teams")]
    [InlineData("/api/teams/{id}/members", "put", "Teams")]
    [InlineData("/api/pca-contacts/all", "get", "PcaContacts")]
    [InlineData("/api/pca-contacts", "post", "PcaContacts")]
    [InlineData("/api/pca-contacts/{id}", "put", "PcaContacts")]
    [InlineData("/api/pca-contacts/{id}/active", "put", "PcaContacts")]
    [InlineData("/api/users/all", "get", "Users")]
    [InlineData("/api/users", "post", "Users")]
    [InlineData("/api/users/{id}", "put", "Users")]
    [InlineData("/api/users/{id}/username", "put", "Users")]
    [InlineData("/api/users/{id}/active", "put", "Users")]
    [InlineData("/api/templates", "get", "Templates")]
    [InlineData("/api/templates", "post", "Templates")]
    [InlineData("/api/templates", "delete", "Templates")]
    [InlineData("/api/templates/{id}", "delete", "Templates")]
    [InlineData("/api/holidays", "get", "Holidays")]
    [InlineData("/api/holidays", "post", "Holidays")]
    [InlineData("/api/holidays/{date}", "delete", "Holidays")]
    [InlineData("/api/default-tasks", "get", "DefaultTasks")]
    [InlineData("/api/default-tasks", "post", "DefaultTasks")]
    [InlineData("/api/default-tasks/{id}/active", "put", "DefaultTasks")]
    [InlineData("/api/default-tasks/sync", "post", "DefaultTasks")]
    [InlineData("/api/standup/entries", "get", "Standup")]
    [InlineData("/api/standup/entries", "post", "Standup")]
    [InlineData("/api/standup/entries/{entryId}", "put", "Standup")]
    [InlineData("/api/standup/entries/{entryId}", "delete", "Standup")]
    [InlineData("/api/standup/entries/reorder", "put", "Standup")]
    [InlineData("/api/standup/board", "get", "Standup")]
    [InlineData("/api/standup/quick-import", "post", "Standup")]
    [InlineData("/api/standup/entries/{entryId}/issues", "post", "Standup")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "put", "Standup")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "delete", "Standup")]
    [InlineData("/api/ops/retention/preview", "post", "Ops")]
    [InlineData("/api/ops/retention/run", "post", "Ops")]
    [InlineData("/api/ops/export/run", "post", "Ops")]
    [InlineData("/api/ops/backup/run", "post", "Ops")]
    public void Route_is_TAGGED_so_ng_openapi_gen_can_include_it(string path, string verb, string tag)
    {
        var tags = _paths.GetProperty(path).GetProperty(verb).GetProperty("tags")
                         .EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains(tag, tags);
    }

    // =============================================================================================
    // M9 P2a — Admin_gated_list_is_NOT_tagged_and_so_never_joins_the_generated_client USED TO LIVE HERE.
    //
    // It asserted that /api/users/all and /api/pca-contacts/all carried NO "Users"/"PcaContacts" tag, and so
    // could never be emitted into the generated client. M8.6 wrote it on an explicit rationale: "no admin-only
    // screen exists, so a typed method for an admin-gated route could only 403 for whoever called it."
    //
    // M9 BUILDS the admin-only screen, which invalidates the premise. USR-01 requires the Users tab to list
    // INACTIVE users -- GET /api/users is GetActiveAsync and can never return one, so "Activate" would have
    // nothing to act on. Only GetAllAsync (/api/users/all) can. Both routes are now tagged, and both DO join
    // the client. The two rows above are the inverse of what this test used to assert.
    //
    // THE GUARD DID NOT GO AWAY -- IT GOT STRONGER, AND IT MOVED.
    //     SettingsEndpointsTests.The_admin_gated_full_list_is_403_for_a_NON_admin
    // This test asserted a PROXY for the security property (the route is absent from OUR generated client --
    // which never stopped anyone holding a cookie and curl). Its replacement asserts the PROPERTY ITSELF: a
    // deliberately NON-admin caller is refused with a 403. If the AdminPolicy is ever dropped from either
    // route, that is what fails -- and unlike this test, it would have failed even in M8.6.
    //
    // The client-side half (an adminGuard on /users and /settings) lands in a later M9 task.
    // =============================================================================================

    // ng-openapi-gen names each generated FUNCTION from the operationId, which in a minimal API comes
    // from .WithName(...). Omit it and Swashbuckle invents one -- so the function gets a name nobody
    // chose, and it churns whenever an unrelated route is added.
    [Theory]
    [InlineData("/api/backlogs", "get", "BacklogList")]
    [InlineData("/api/backlogs", "post", "BacklogCreate")]
    [InlineData("/api/backlogs/{id}", "get", "BacklogGet")]
    [InlineData("/api/backlogs/{id}", "put", "BacklogUpdate")]
    [InlineData("/api/backlogs/{id}/audit", "get", "BacklogAudit")]
    [InlineData("/api/tasks", "get", "TaskList")]
    [InlineData("/api/tasks", "post", "TaskCreate")]
    [InlineData("/api/tasks/{id}", "put", "TaskUpdate")]
    [InlineData("/api/tasks/{id}/active", "put", "TaskSetActive")]
    [InlineData("/api/tasks/{id}/order", "put", "TaskSetOrder")]
    [InlineData("/api/users", "get", "UserListActive")]
    [InlineData("/api/users/names", "get", "UserNames")]
    [InlineData("/api/pca-contacts", "get", "PcaContactListActive")]
    [InlineData("/api/pca-contacts/names", "get", "PcaContactNames")]
    // ---- M9 P2: SettingsEndpoints.
    [InlineData("/api/me/active-team", "put", "MeSetActiveTeam")]
    [InlineData("/api/tags", "get", "TagList")]
    [InlineData("/api/tags", "post", "TagCreate")]
    [InlineData("/api/tags/{id}", "put", "TagUpdate")]
    [InlineData("/api/tags/{id}", "delete", "TagDelete")]
    [InlineData("/api/teams", "get", "TeamListActive")]
    [InlineData("/api/teams", "post", "TeamCreate")]
    [InlineData("/api/teams/all", "get", "TeamListAll")]
    [InlineData("/api/teams/{id}", "put", "TeamRename")]
    [InlineData("/api/teams/{id}/active", "put", "TeamSetActive")]
    [InlineData("/api/teams/{id}/members", "get", "TeamMembers")]
    [InlineData("/api/teams/{id}/members", "put", "TeamSetMembers")]
    [InlineData("/api/pca-contacts/all", "get", "PcaContactListAll")]
    [InlineData("/api/pca-contacts", "post", "PcaContactCreate")]
    [InlineData("/api/pca-contacts/{id}", "put", "PcaContactRename")]
    [InlineData("/api/pca-contacts/{id}/active", "put", "PcaContactSetActive")]
    [InlineData("/api/users/all", "get", "UserListAll")]
    [InlineData("/api/users", "post", "UserCreate")]
    [InlineData("/api/users/{id}", "put", "UserRename")]
    [InlineData("/api/users/{id}/username", "put", "UserSetUsername")]
    [InlineData("/api/users/{id}/active", "put", "UserSetActive")]
    [InlineData("/api/templates", "get", "TemplateList")]
    [InlineData("/api/templates", "post", "TemplateCreate")]
    [InlineData("/api/templates", "delete", "TemplateDeleteByName")]
    [InlineData("/api/templates/{id}", "delete", "TemplateDelete")]
    [InlineData("/api/holidays", "get", "HolidayList")]
    [InlineData("/api/holidays", "post", "HolidayUpsert")]
    [InlineData("/api/holidays/{date}", "delete", "HolidayDelete")]
    [InlineData("/api/default-tasks", "get", "DefaultTaskList")]
    [InlineData("/api/default-tasks", "post", "DefaultTaskCreate")]
    [InlineData("/api/default-tasks/{id}/active", "put", "DefaultTaskSetActive")]
    [InlineData("/api/default-tasks/sync", "post", "DefaultTaskSync")]
    [InlineData("/api/standup/entries", "get", "StandupMyDay")]
    [InlineData("/api/standup/entries", "post", "StandupEntryCreate")]
    [InlineData("/api/standup/entries/{entryId}", "put", "StandupEntryUpdate")]
    [InlineData("/api/standup/entries/{entryId}", "delete", "StandupEntryDelete")]
    [InlineData("/api/standup/entries/reorder", "put", "StandupEntryReorder")]
    [InlineData("/api/standup/board", "get", "StandupBoard")]
    [InlineData("/api/standup/quick-import", "post", "StandupQuickImport")]
    [InlineData("/api/standup/entries/{entryId}/issues", "post", "StandupIssueCreate")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "put", "StandupIssueUpdate")]
    [InlineData("/api/standup/entries/{entryId}/issues/{issueId}", "delete", "StandupIssueDelete")]
    [InlineData("/api/ops/retention/preview", "post", "OpsRetentionPreview")]
    [InlineData("/api/ops/retention/run", "post", "OpsRetentionRun")]
    [InlineData("/api/ops/export/run", "post", "OpsExportRun")]
    [InlineData("/api/ops/backup/run", "post", "OpsBackupRun")]
    public void Route_has_the_operationId_the_generated_function_is_named_from(
        string path, string verb, string operationId)
    {
        var op = _paths.GetProperty(path).GetProperty(verb);
        Assert.True(op.TryGetProperty("operationId", out var id),
            $"{verb.ToUpperInvariant()} {path} has no operationId -- add .WithName(\"{operationId}\").");
        Assert.Equal(operationId, id.GetString());
    }
}
