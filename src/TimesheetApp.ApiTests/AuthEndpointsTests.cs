using System.Net;
using System.Net.Http.Json;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>W2-A: <c>/api/auth/set-password</c> (self) and <c>/api/auth/users/{id}/set-password</c> (admin).
///
/// <para>Login/logout/`/api/me` are AuthTests's territory (the mechanism, owned by <c>Auth/AuthSetup.cs</c>).
/// This file only exercises the two feature routes <c>AuthEndpoints.cs</c> maps.</para></summary>
public sealed class AuthEndpointsTests
{
    // ---- Self set-password --------------------------------------------------------------------------

    [Fact]
    public async Task Self_set_password_makes_the_old_password_stop_working_and_the_new_one_work()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new AuthSetPasswordRequest("NewPass1!"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", "NewPass1!"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>THE IDOR CHECK. <c>AuthSetPasswordRequest</c> has no <c>userId</c> property, so a client
    /// cannot express "change someone else's password" through the typed contract — but a hand-built JSON
    /// body could still smuggle an extra <c>userId</c> field past the deserializer. This proves it is inert:
    /// the actor is ALWAYS <c>ctx.UserId</c>, never anything read off the wire.</summary>
    [Fact]
    public async Task Self_set_password_ignores_any_userId_smuggled_into_the_body()
    {
        using var factory = new ApiFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);

        using var client = await factory.ClientAsync("alice");

        // A raw anonymous object, not the typed DTO: this is the only way to even ATTEMPT sending a userId
        // the contract does not declare.
        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new { newPassword = "NewPass1!", userId = bobId });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        // Alice's password changed...
        var aliceNewLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", "NewPass1!"));
        Assert.Equal(HttpStatusCode.OK, aliceNewLogin.StatusCode);

        // ...and Bob's did NOT, despite his id riding along in the JSON body.
        var bobStillOld = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("bob", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.OK, bobStillOld.StatusCode);

        Assert.NotEqual(aliceId, bobId); // sanity: two distinct users were actually seeded
    }

    [Fact]
    public async Task Self_set_password_rejects_an_empty_new_password()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new AuthSetPasswordRequest(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Error));
    }

    [Fact]
    public async Task Anonymous_gets_401_on_self_set_password()
    {
        using var factory = new ApiFactory();
        using var client = factory.AnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new AuthSetPasswordRequest("NewPass1!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Admin set-password-for-user -----------------------------------------------------------------

    [Fact]
    public async Task Admin_can_set_another_users_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest("NewPass1!"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("target", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("target", "NewPass1!"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>THE AUTHENTICATION-BYPASS SHAPE, exercised through THIS wave's own write path rather than
    /// just at login. A NULL <c>password_hash</c> must mean "cannot log in" both before AND after an admin
    /// interaction that does not target this user, and setting a real password must be the only thing that
    /// changes that.</summary>
    [Fact]
    public async Task Admin_resetting_a_never_logged_in_user_lets_them_log_in_only_with_the_new_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserWithoutPasswordAsync("neverset");
        using var anon = factory.AnonymousClient();

        // Before: NULL password_hash means "cannot log in", never "any password matches".
        foreach (var attempt in new[] { "", "anything", ApiFactory.DefaultPassword })
        {
            var before = await anon.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest("neverset", attempt));
            Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);
        }

        using var admin = await factory.AdminClientAsync();
        var response = await admin.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest("NewPass1!"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // After: only the new password works.
        var wrongAfter = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("neverset", "anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAfter.StatusCode);

        var rightAfter = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("neverset", "NewPass1!"));
        Assert.Equal(HttpStatusCode.OK, rightAfter.StatusCode);
    }

    [Fact]
    public async Task Non_admin_gets_403_setting_another_users_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("peon");

        var response = await client.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest("NewPass1!"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_admin_set_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        using var client = factory.AnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest("NewPass1!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_set_password_on_an_unknown_user_returns_404()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/auth/users/999999/set-password", new AuthAdminSetPasswordRequest("NewPass1!"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_set_password_rejects_an_empty_new_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Error));
    }
}
