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
    [InlineData("/api/tasks", "post", "200")]
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

    [Theory]
    [InlineData("/api/backlogs", "get", "Backlogs")]
    [InlineData("/api/backlogs", "post", "Backlogs")]
    [InlineData("/api/backlogs/{id}", "get", "Backlogs")]
    [InlineData("/api/backlogs/{id}", "put", "Backlogs")]
    [InlineData("/api/backlogs/{id}/audit", "get", "Backlogs")]
    [InlineData("/api/tasks", "post", "Tasks")]
    [InlineData("/api/tasks/{id}/active", "put", "Tasks")]
    [InlineData("/api/tasks/{id}/order", "put", "Tasks")]
    public void Route_is_TAGGED_so_ng_openapi_gen_can_include_it(string path, string verb, string tag)
    {
        var tags = _paths.GetProperty(path).GetProperty(verb).GetProperty("tags")
                         .EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains(tag, tags);
    }

    // ng-openapi-gen names each generated FUNCTION from the operationId, which in a minimal API comes
    // from .WithName(...). Omit it and Swashbuckle invents one -- so the function gets a name nobody
    // chose, and it churns whenever an unrelated route is added.
    [Theory]
    [InlineData("/api/backlogs", "get", "BacklogList")]
    [InlineData("/api/backlogs", "post", "BacklogCreate")]
    [InlineData("/api/backlogs/{id}", "get", "BacklogGet")]
    [InlineData("/api/backlogs/{id}", "put", "BacklogUpdate")]
    [InlineData("/api/backlogs/{id}/audit", "get", "BacklogAudit")]
    [InlineData("/api/tasks", "post", "TaskCreate")]
    [InlineData("/api/tasks/{id}/active", "put", "TaskSetActive")]
    [InlineData("/api/tasks/{id}/order", "put", "TaskSetOrder")]
    public void Route_has_the_operationId_the_generated_function_is_named_from(
        string path, string verb, string operationId)
    {
        var op = _paths.GetProperty(path).GetProperty(verb);
        Assert.True(op.TryGetProperty("operationId", out var id),
            $"{verb.ToUpperInvariant()} {path} has no operationId -- add .WithName(\"{operationId}\").");
        Assert.Equal(operationId, id.GetString());
    }
}
