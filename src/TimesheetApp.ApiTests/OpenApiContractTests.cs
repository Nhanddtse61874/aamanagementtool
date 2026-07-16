using System.Net;
using System.Text.Json;
using Xunit;                       // NOT covered by ImplicitUsings -- every test file here has it explicitly

namespace TimesheetApp.ApiTests;

/// <summary>Boots the API host ONCE for the whole of <see cref="OpenApiContractTests"/> and fetches the
/// swagger document ONCE.
///
/// <para><b>Why this exists (M9 P2.5).</b> xUnit constructs a fresh instance of a test class for EVERY test
/// CASE — and a <c>[Theory]</c> row is a case. <see cref="OpenApiContractTests"/> used to implement
/// <c>IAsyncLifetime</c> directly, so its <c>InitializeAsync</c> — <c>new ApiFactory()</c> plus a swagger
/// fetch — ran once per <c>[InlineData]</c> row. At 212 rows that was 212 full ASP.NET host boots for a
/// document that is IDENTICAL every time. M9 P2 measured the cost as it grew: ApiTests went from 43s to
/// 1m59s. A <c>IClassFixture&lt;T&gt;</c> is instantiated once per test class and shared by every case, so
/// the boot count drops from "one per row" to exactly one.</para>
///
/// <para><b>Why sharing is SAFE here, and would not be for an endpoint test.</b> The swagger document is
/// immutable for a given build, and these tests only READ it — nothing mutates the host, the database or the
/// document. There is no per-row isolation to lose. Endpoint tests are the opposite (they seed and write), so
/// they keep their own <c>ApiFactory</c> per test; do not "optimise" those the same way.</para>
///
/// <para><b>It exposes the PARSED paths, not the factory.</b> Fetching and parsing once is strictly better
/// than booting once and re-fetching per row, and handing out only a <see cref="JsonElement"/> means no test
/// can reach back into the host and mutate shared state. The element is <c>Clone()</c>d, which detaches it
/// from the <c>JsonDocument</c>, so the document is disposed immediately rather than held for the run.</para>
///
/// <para>Each boot is also another roll of the dice on the known host-startup race (an intermittent
/// <c>no such table: Backlogs</c>), so collapsing 212 boots into 1 removes 211 chances to flake.</para></summary>
public sealed class SwaggerFixture : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    /// <summary>The <c>paths</c> object of <c>/swagger/v1/swagger.json</c>.</summary>
    public JsonElement Paths { get; private set; }

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();
        var res = await _factory.AnonymousClient().GetAsync("/swagger/v1/swagger.json");  // deliberately anonymous
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var document = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Paths = document.RootElement.GetProperty("paths").Clone();
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }
}

/// <summary>
/// The generated TypeScript client can only contain a route whose C# DECLARES a response type. A route
/// that returns IResult via Results.Ok(x) and says nothing else is described by OpenAPI as an empty 200,
/// and codegen then emits a method typed `void` for an endpoint that returns data. That failure is
/// SILENT -- codegen SUCCEEDS. This test is what makes the rule enforceable.
/// </summary>
public sealed class OpenApiContractTests : IClassFixture<SwaggerFixture>
{
    private readonly JsonElement _paths;

    public OpenApiContractTests(SwaggerFixture swagger) => _paths = swagger.Paths;

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
    [InlineData("/api/default-tasks/all", "get", "200")]
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
    // ---- M9 P2.5: the 8 routes BacklogEndpoints.cs had left unannotated. The two headline features of this
    // milestone are built ON them -- the TagPicker (both tag routes on each entity) and the Task List's
    // Continue button + inline status/type/assignee editors -- so an empty 200 here is not a cosmetic gap:
    // it is the data those screens are made of, invisible to the generated client.
    //
    // Both tag READS return a BARE ARRAY OF INTS (GetTagIdsAsync -> IReadOnlyList<int>), NOT a list of
    // TagDto: the picker resolves the ids against the tag list it already holds. A wrong .Produces<T>() here
    // would generate a TYPED LIE -- worse than no method at all.
    [InlineData("/api/backlogs/{id}/tags", "get", "200")]
    [InlineData("/api/backlogs/{id}/tags", "put", "200")]
    [InlineData("/api/backlogs/{id}/tags", "put", "409")]
    // POST /continue has TWO distinct 400 paths (empty TargetPeriod; the code already exists in the target
    // period, where ContinueAsync returns 0 rather than throwing) and NO 409 -- it takes no expectedVersion.
    [InlineData("/api/backlogs/{id}/continue", "post", "200")]
    [InlineData("/api/backlogs/{id}/continue", "post", "400")]
    [InlineData("/api/tasks/{id}", "get", "200")]
    [InlineData("/api/tasks/{id}/status", "put", "200")]
    [InlineData("/api/tasks/{id}/status", "put", "400")]
    [InlineData("/api/tasks/{id}/status", "put", "409")]
    // /extended declares NO 400: both fields are nullable and the handler has no validation guard, so
    // clearing either is legitimate. Declaring one would be a status the route cannot return.
    [InlineData("/api/tasks/{id}/extended", "put", "200")]
    [InlineData("/api/tasks/{id}/extended", "put", "409")]
    [InlineData("/api/tasks/{id}/tags", "get", "200")]
    [InlineData("/api/tasks/{id}/tags", "put", "200")]
    [InlineData("/api/tasks/{id}/tags", "put", "409")]
    // ---- M9 P3: Reports + the five new routes. The five Reports/Export handlers already EXISTED and
    // already worked -- they ended bare at `});` with no .WithName/.WithTags/.Produces at all, so the
    // document described them as an empty 200 under the assembly-name default tag and the generated client
    // could not see them. Nothing about their behaviour changes here; only the document does.
    //
    // /api/export/* is deliberately ABSENT from this theory: it returns a binary xlsx and a text/markdown
    // string, NEITHER of which is application/json. Asserting a JSON schema on them would fail, and forcing
    // one would be a typed lie. They are pinned by Non_JSON_route_declares_the_media_type_it_actually_serves.
    [InlineData("/api/reports/weekly", "get", "200")]
    [InlineData("/api/reports/monthly", "get", "200")]
    [InlineData("/api/reports/missing-logs", "get", "200")]
    [InlineData("/api/tasklist", "get", "200")]
    // The route CONSTRAINT is stripped by ApiExplorer: the C# declares "{id:int}" but the document key is
    // "{id}" (see the note at the top of this theory).
    [InlineData("/api/users/{id}/admin", "put", "200")]
    [InlineData("/api/users/{id}/admin", "put", "409")]
    [InlineData("/api/settings/{key}", "get", "200")]
    [InlineData("/api/settings/{key}", "put", "400")]
    [InlineData("/api/standup/archive", "post", "200")]
    [InlineData("/api/standup/archive", "post", "400")]
    // 400 only: the 200 is text/markdown, pinned in the non-JSON theory below.
    [InlineData("/api/tasklist/export", "get", "400")]
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

    /// <summary>M9 P3. The three routes that do NOT serve JSON, and must not claim to.
    ///
    /// <para><c>/api/export/excel</c> is <c>Results.File</c> — raw xlsx bytes. <c>/api/export/markdown</c> and
    /// <c>/api/tasklist/export</c> are <c>Results.Text</c> — a markdown document. An
    /// <c>application/json</c> schema on any of them would be a TYPED LIE: <c>ng-openapi-gen</c> would emit a
    /// client that calls <c>response.json()</c> on a spreadsheet.</para>
    ///
    /// <para>They are annotated anyway, because a route that says NOTHING about what it returns is
    /// indistinguishable in the document from a route that returns nothing. The <c>Export</c> tag is
    /// separately excluded from <c>ng-openapi-gen.json</c>'s <c>includeTags</c>, so no client method is
    /// generated for the two export routes regardless — the document is simply honest about them now.</para></summary>
    [Theory]
    [InlineData("/api/export/excel", "get",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("/api/export/markdown", "get", "text/markdown")]
    [InlineData("/api/tasklist/export", "get", "text/markdown")]
    public void Non_JSON_route_declares_the_media_type_it_actually_serves(
        string path, string verb, string mediaType)
    {
        var response = _paths.GetProperty(path).GetProperty(verb)
                             .GetProperty("responses").GetProperty("200");

        Assert.True(response.TryGetProperty("content", out var content),
            $"{verb.ToUpperInvariant()} {path} declares 200 with NO content at all.");
        Assert.True(content.TryGetProperty(mediaType, out _),
            $"{verb.ToUpperInvariant()} {path} must declare {mediaType} -- that is what it actually serves.");
        Assert.False(content.TryGetProperty("application/json", out _),
            $"{verb.ToUpperInvariant()} {path} must NOT claim application/json -- it serves {mediaType}.");
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
    // ---- M9 P3. Settings is DELIBERATELY UNVERSIONED (DatabaseInitializer: "key/date-keyed -- last-write-wins
    // IS the correct semantics there"), so the write takes no expectedVersion, has no version to hand back and
    // cannot 409. 204, not 200 with a SavedBody.
    [InlineData("/api/settings/{key}", "put", "204")]
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
    [InlineData("/api/default-tasks/all", "get", "DefaultTasks")]
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
    // ---- M9 P2.5: the 8 previously-unannotated routes. "Backlogs" and "Tasks" were ALREADY in
    // ng-openapi-gen.json's includeTags (the annotated routes on this file carry them), so these eight were
    // the only ones in their own file falling through to the assembly-name default tag -- and ng-openapi-gen
    // selects BY TAG, so all eight were omitted from the generated client while their siblings were emitted.
    [InlineData("/api/backlogs/{id}/tags", "get", "Backlogs")]
    [InlineData("/api/backlogs/{id}/tags", "put", "Backlogs")]
    [InlineData("/api/backlogs/{id}/continue", "post", "Backlogs")]
    [InlineData("/api/tasks/{id}", "get", "Tasks")]
    [InlineData("/api/tasks/{id}/status", "put", "Tasks")]
    [InlineData("/api/tasks/{id}/extended", "put", "Tasks")]
    [InlineData("/api/tasks/{id}/tags", "get", "Tasks")]
    [InlineData("/api/tasks/{id}/tags", "put", "Tasks")]
    // ---- M9 P3. Five NEW tags -- Reports, Export, TaskList, Settings -- plus new routes joining the existing
    // Users and Standup tags. Adding a tag to the DOCUMENT is only half the job: ng-openapi-gen selects
    // operations BY TAG, so each new tag must also be added to includeTags in ng-openapi-gen.json or its
    // routes are silently omitted from the generated client. That half is P4's.
    //
    // "Export" is the deliberate exception: its two routes serve xlsx bytes and a markdown string, which
    // ng-openapi-gen cannot type usefully, so the tag exists for the document's honesty and is NOT expected
    // to join includeTags.
    [InlineData("/api/reports/weekly", "get", "Reports")]
    [InlineData("/api/reports/monthly", "get", "Reports")]
    [InlineData("/api/reports/missing-logs", "get", "Reports")]
    [InlineData("/api/export/excel", "get", "Export")]
    [InlineData("/api/export/markdown", "get", "Export")]
    [InlineData("/api/tasklist", "get", "TaskList")]
    [InlineData("/api/tasklist/export", "get", "TaskList")]
    [InlineData("/api/users/{id}/admin", "put", "Users")]
    [InlineData("/api/settings/{key}", "get", "Settings")]
    [InlineData("/api/settings/{key}", "put", "Settings")]
    [InlineData("/api/standup/archive", "post", "Standup")]
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
    [InlineData("/api/default-tasks/all", "get", "DefaultTaskListAll")]
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
    // ---- M9 P2.5: the 8 previously-unannotated routes. Names follow the conventions already set in this
    // file: <Entity>Get for a read-by-id (BacklogGet), <Entity>Set<Field> for a sub-field write
    // (TaskSetActive / TaskSetOrder), and the <Entity><Collection> / <Entity>Set<Collection> pair for a
    // sub-collection read+write (TeamMembers / TeamSetMembers).
    [InlineData("/api/backlogs/{id}/tags", "get", "BacklogTags")]
    [InlineData("/api/backlogs/{id}/tags", "put", "BacklogSetTags")]
    [InlineData("/api/backlogs/{id}/continue", "post", "BacklogContinue")]
    [InlineData("/api/tasks/{id}", "get", "TaskGet")]
    [InlineData("/api/tasks/{id}/status", "put", "TaskSetStatus")]
    [InlineData("/api/tasks/{id}/extended", "put", "TaskSetExtended")]
    [InlineData("/api/tasks/{id}/tags", "get", "TaskTags")]
    [InlineData("/api/tasks/{id}/tags", "put", "TaskSetTags")]
    // ---- M9 P3.
    //
    // "TaskListScreen", NOT "TaskList": `.WithName("TaskList")` is ALREADY TAKEN by GET /api/tasks
    // (BacklogEndpoints.cs). Endpoint names must be unique across the whole app -- a duplicate is an
    // InvalidOperationException at startup, not a compile error, so it would take down every route in the
    // API and not just this one. The name also reads correctly: this route returns the whole SCREEN (rows +
    // Gantt from one snapshot), which is a different thing from the /api/tasks list.
    [InlineData("/api/reports/weekly", "get", "ReportsWeekly")]
    [InlineData("/api/reports/monthly", "get", "ReportsMonthly")]
    [InlineData("/api/reports/missing-logs", "get", "ReportsMissingLogs")]
    [InlineData("/api/export/excel", "get", "ExportExcel")]
    [InlineData("/api/export/markdown", "get", "ExportMarkdown")]
    [InlineData("/api/tasklist", "get", "TaskListScreen")]
    [InlineData("/api/tasklist/export", "get", "TaskListExport")]
    [InlineData("/api/users/{id}/admin", "put", "UserSetAdmin")]
    [InlineData("/api/settings/{key}", "get", "SettingGet")]
    [InlineData("/api/settings/{key}", "put", "SettingSet")]
    [InlineData("/api/standup/archive", "post", "StandupArchiveWeek")]
    public void Route_has_the_operationId_the_generated_function_is_named_from(
        string path, string verb, string operationId)
    {
        var op = _paths.GetProperty(path).GetProperty(verb);
        Assert.True(op.TryGetProperty("operationId", out var id),
            $"{verb.ToUpperInvariant()} {path} has no operationId -- add .WithName(\"{operationId}\").");
        Assert.Equal(operationId, id.GetString());
    }

    /// <summary>M9 P4.5. The four routes that take a team filter — and could not RECEIVE one from the
    /// generated client, because nothing in their C# told ApiExplorer the parameter existed.
    ///
    /// <para>Each of these handlers resolves its team scope through
    /// <c>TimesheetEndpoints.EffectiveTeamIds</c>, which reads <c>teamIds</c> off
    /// <c>HttpContext.Request.Query</c> BY HAND. That is deliberate and load-bearing: a bound <c>int[]?</c>
    /// CANNOT TELL "key absent" (⇒ the caller's own teams) from "key present but empty" (⇒ no teams) — both
    /// bind to an EMPTY ARRAY, never null, while <c>null</c> means EVERY TEAM to the repository. It is a
    /// data-leak guard and it stays.</para>
    ///
    /// <para>But ApiExplorer cannot see a parameter that the handler signature never declares. So the document
    /// omitted <c>teamIds</c> on all four, <c>TaskListScreen$Params</c> was <c>{year, month}</c>, and the team
    /// filter four screens depend on COULD NOT BE SENT AT ALL. Each route therefore declares a
    /// bound-but-deliberately-unread <c>[FromQuery] int[]? teamIds</c> whose only job is to appear here.</para>
    ///
    /// <para><b>This test exists because that parameter is indistinguishable from dead code.</b> Deleting it
    /// breaks no compile, no runtime path and no other test — every handler goes on reading the raw query, so
    /// the API keeps working perfectly. The only casualty is the generated client, on the next regeneration,
    /// silently. THIS ASSERTION IS THE ONLY THING THAT GOES RED. Do not delete it either.</para></summary>
    [Theory]
    [InlineData("/api/tasklist", "get")]
    [InlineData("/api/tasklist/export", "get")]
    [InlineData("/api/reports/weekly", "get")]
    [InlineData("/api/reports/monthly", "get")]
    public void Team_scoped_route_DECLARES_teamIds_so_the_generated_client_can_send_it(string path, string verb)
    {
        var op = _paths.GetProperty(path).GetProperty(verb);

        Assert.True(op.TryGetProperty("parameters", out var parameters),
            $"{verb.ToUpperInvariant()} {path} declares NO parameters at all -- `teamIds` is gone.");

        var teamIds = parameters.EnumerateArray()
            .SingleOrDefault(p => p.GetProperty("name").GetString() == "teamIds");

        Assert.True(teamIds.ValueKind is not JsonValueKind.Undefined,
            $"{verb.ToUpperInvariant()} {path} does not declare a `teamIds` parameter. The handler still reads " +
            "it through EffectiveTeamIds, so the API itself is fine -- but ApiExplorer cannot see it, the " +
            "generated client cannot send it, and this screen's team filter is silently dead. Restore the " +
            "bound-but-unread `[FromQuery] int[]? teamIds` on the handler; do NOT make the handler read it.");

        Assert.Equal("query", teamIds.GetProperty("in").GetString());

        // ARRAY, not a scalar. The wire format is a REPEATED KEY (?teamIds=1&teamIds=2) -- that is what
        // EffectiveTeamIds parses, and what RequestBuilder emits for an array (style: form, explode: true, one
        // query entry per element). A scalar schema here would make the client send "1,2" as a SINGLE value,
        // which int.TryParse rejects, which EffectiveTeamIds then drops -- leaving NO teams and a blank screen.
        Assert.Equal("array", teamIds.GetProperty("schema").GetProperty("type").GetString());
    }
}
