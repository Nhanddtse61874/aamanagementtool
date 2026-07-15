using System.Net;
using System.Net.Http.Json;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>v11 — a duplicate username must be a clean 409, never the 500 it used to be.
///
/// <para>Before v11 <c>Users.username</c> had no unique index, so two users could share one. The login
/// lookups (<c>GetCredentialsAsync</c> / <c>GetByUsernameAsync</c>, both <c>QuerySingleOrDefault</c>) then
/// THREW on the second row — a 500 that locked BOTH of them out. The reachable HTTP path that could create
/// the clash is <c>PUT /api/users/{id}/username</c>; these tests prove it now 409s at the pre-check, and the
/// DB index + <c>ExceptionMapper</c> stand behind it for the race the pre-check cannot see.</para></summary>
public sealed class UserUsernameUniquenessTests
{
    private static async Task<UserDto> CreateUserAsync(HttpClient admin, string name)
    {
        var resp = await admin.PostAsJsonAsync("/api/users", new SettingsNameRequest(name));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<UserDto>())!;
    }

    private static async Task<long> SetUsernameOkAsync(HttpClient admin, int userId, string username, long version)
    {
        var resp = await admin.PutAsJsonAsync(
            $"/api/users/{userId}/username", new SettingsUserSetUsernameRequest(username, version));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SavedBody>())!.RowVersion;
    }

    [Theory]
    [InlineData("nhan")]  // the exact username Bob holds
    [InlineData("NHAN")]  // and a case variant of it — NOCASE uniqueness catches this too
    public async Task PUT_username_onto_an_existing_username_is_409_not_500(string attempt)
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var bob = await CreateUserAsync(admin, "Bob");
        await SetUsernameOkAsync(admin, bob.Id, "nhan", bob.RowVersion);

        var carol = await CreateUserAsync(admin, "Carol");

        var resp = await admin.PutAsJsonAsync(
            $"/api/users/{carol.Id}/username", new SettingsUserSetUsernameRequest(attempt, carol.RowVersion));

        // THE BUG: this used to be a 500 (or, pre-index, a silent second 'nhan' that 500'd login forever).
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.Contains("taken", body!.Message, StringComparison.OrdinalIgnoreCase);

        // Carol was left untouched — no half-applied write.
        var users = await (await admin.GetAsync("/api/users")).Content.ReadFromJsonAsync<List<UserDto>>();
        Assert.Null(users!.Single(u => u.Id == carol.Id).Username);
    }

    [Fact] // The self-case-change edge: re-setting your OWN username to a case variant is not a conflict.
    public async Task PUT_username_to_a_case_variant_of_your_own_is_allowed()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var bob = await CreateUserAsync(admin, "Bob");
        var v2 = await SetUsernameOkAsync(admin, bob.Id, "bob", bob.RowVersion);

        // "bob" -> "BOB" on the same row: the pre-check excludes this user, and the index sees no other row.
        var resp = await admin.PutAsJsonAsync(
            $"/api/users/{bob.Id}/username", new SettingsUserSetUsernameRequest("BOB", v2));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task POST_api_users_twice_with_the_same_name_both_succeed_with_a_null_username()
    {
        // NOTE (brief correction): the brief lists POST /api/users as a duplicate-username path. It is not.
        // POST takes only a name (SettingsNameRequest) and ALWAYS inserts a NULL username — a created user
        // cannot log in until an admin sets a username via PUT. So POST can never create a duplicate; the
        // only reachable clash is the PUT path covered above. This test pins that behaviour.
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var first = await CreateUserAsync(admin, "Same Name");
        var second = await CreateUserAsync(admin, "Same Name");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.Username);
        Assert.Null(second.Username);
    }
}
