using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Models;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>W2-A: <c>/api/auth/set-password</c> (self) and <c>/api/auth/users/{id}/set-password</c> (admin),
/// plus the <c>AuthSetup.VerifyPassword</c> primitive they rest on.
///
/// <para>Login/logout/<c>/api/me</c> are AuthTests's territory (the mechanism, owned by
/// <c>Auth/AuthSetup.cs</c>). This file only exercises the two feature routes <c>AuthEndpoints.cs</c> maps —
/// and the one method W2-A added to <c>AuthSetup</c> to make the self route's current-password check
/// possible.</para></summary>
public sealed class AuthEndpointsTests
{
    private const string NewPassword = "NewPass1!";

    // ---- AuthSetup.VerifyPassword: the primitive --------------------------------------------------------

    [Fact]
    public void VerifyPassword_accepts_the_right_password_and_rejects_everything_else()
    {
        var hash = AuthSetup.HashPassword(ApiFactory.DefaultPassword);

        Assert.True(AuthSetup.VerifyPassword(hash, ApiFactory.DefaultPassword));
        Assert.False(AuthSetup.VerifyPassword(hash, "wrong"));
        Assert.False(AuthSetup.VerifyPassword(hash, ""));
        Assert.False(AuthSetup.VerifyPassword(hash, null));
    }

    /// <summary>THE AUTHENTICATION-BYPASS SHAPE, at the primitive. A null/empty <c>password_hash</c> means
    /// "has never had a password set" — the state EVERY user is in on a freshly migrated v10 database — and
    /// it must NEVER verify, for any candidate, including the empty string an empty form field sends.
    /// <c>VerifyHashedPassword</c> THROWS on a null hash rather than returning Failed, so fail-closed here is
    /// not decoration: it is what keeps a forgetful future caller from getting an exception instead of a
    /// clean "no".</summary>
    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    [InlineData(ApiFactory.DefaultPassword)]
    public void VerifyPassword_fails_closed_on_a_null_or_empty_hash(string candidate)
    {
        Assert.False(AuthSetup.VerifyPassword(null, candidate));
        Assert.False(AuthSetup.VerifyPassword("", candidate));
    }

    /// <summary>PROVES the three-outcome claim rather than trusting it. <c>VerifyHashedPassword</c> returns
    /// Failed / Success / <b>SuccessRehashNeeded</b>, and the third one IS A SUCCESS: it means the stored
    /// hash was written under older options (here an IdentityV2 format marker; a lower iteration count does
    /// the same) and should be re-hashed some day — NOT that the password was wrong.
    ///
    /// <para>If <c>VerifyPassword</c> compared <c>== Success</c> instead of <c>!= Failed</c>, this test fails
    /// — and in production every user whose hash predates a hasher-options change would be locked out of
    /// their own account with a "current password is incorrect" they could never satisfy.</para>
    ///
    /// <para>The locally-constructed hasher here is NOT a violation of "never construct your own
    /// PasswordHasher": it exists purely to MANUFACTURE a legacy-format hash as a test fixture, which is the
    /// only way to produce a SuccessRehashNeeded input. Production code still verifies through the one shared
    /// instance inside AuthSetup.</para></summary>
    [Fact]
    public void VerifyPassword_treats_SuccessRehashNeeded_as_a_success()
    {
        var legacyHasher = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        }));
        var legacyHash = legacyHasher.HashPassword(new User(0, "", null, true), ApiFactory.DefaultPassword);

        // Sanity: this really is the SuccessRehashNeeded path, not a plain Success.
        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            new PasswordHasher<User>().VerifyHashedPassword(
                new User(0, "", null, true), legacyHash, ApiFactory.DefaultPassword));

        Assert.True(AuthSetup.VerifyPassword(legacyHash, ApiFactory.DefaultPassword));
        Assert.False(AuthSetup.VerifyPassword(legacyHash, "wrong"));
    }

    // ---- Self set-password ------------------------------------------------------------------------------

    [Fact]
    public async Task Self_set_password_makes_the_old_password_stop_working_and_the_new_one_work()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password",
            new AuthSetPasswordRequest(ApiFactory.DefaultPassword, NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", NewPassword));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>THE SESSION-TAKEOVER GUARD. A live session must not be enough to change the password on its
    /// own: an unattended laptop or a stolen cookie is temporary, but a password change converts it into a
    /// permanent account takeover. Wrong current password =&gt; 400, and — the half that actually matters —
    /// THE PASSWORD IS UNCHANGED, proven by logging in with both candidates afterwards.
    ///
    /// <para>400, not 401: the caller IS authenticated; it is the password that is wrong. A 401 would tell
    /// the SPA the session died and bounce the user to the login screen over a typo.</para></summary>
    [Fact]
    public async Task Self_set_password_with_the_wrong_current_password_is_400_and_leaves_the_password_unchanged()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new AuthSetPasswordRequest("not-the-password", NewPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidationBody>();
        Assert.False(string.IsNullOrWhiteSpace(body!.Error));

        using var anon = factory.AnonymousClient();

        // Nothing was written: the ORIGINAL password still works...
        var stillOld = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.OK, stillOld.StatusCode);

        // ...and the password the caller tried to set was never applied.
        var neverSet = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, neverSet.StatusCode);
    }

    /// <summary>A missing <c>currentPassword</c> is a FAILED PROOF, not a skipped check. The C# record says
    /// the field is there, but JSON can simply omit it — so this posts a body that has no
    /// <c>currentPassword</c> property at all.</summary>
    [Fact]
    public async Task Self_set_password_with_no_current_password_at_all_is_400_and_changes_nothing()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");

        var response = await client.PostAsJsonAsync(
            "/api/auth/set-password", new { newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var anon = factory.AnonymousClient();
        var stillOld = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.OK, stillOld.StatusCode);
    }

    /// <summary>THE AUTHENTICATION-BYPASS SHAPE, through THIS wave's own write path. A user whose stored hash
    /// is NULL has no current password to prove, so self-service must REJECT them (they need an admin reset)
    /// — never wave them through on the reasoning "there is nothing to check against".
    ///
    /// <para>Reaching this state needs a live session over a NULL hash, which login cannot produce (it
    /// rejects a null hash outright — that is AuthTests's job). So: log in normally, then clear the hash out
    /// of band. That is exactly the real-world shape — a session that predates the hash being cleared.</para></summary>
    [Fact]
    public async Task Self_set_password_is_rejected_when_the_stored_hash_is_null()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        using var client = await factory.ClientAsync("alice");   // a live session...

        // ...whose stored hash is then cleared. The cookie remains valid: ClientContextFilter resolves the
        // user by username, and the row still exists.
        using (var db = factory.OpenDb())
        {
            await db.ExecuteAsync(
                "UPDATE Users SET password_hash = NULL WHERE id = @id;", new { id = userId });
        }

        // Empty string is included on purpose: it is what an empty form field sends, and it is the exact
        // input a naive "no hash => nothing to compare => allow" would wave through.
        foreach (var attempt in new[] { "", "anything", ApiFactory.DefaultPassword })
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/set-password", new AuthSetPasswordRequest(attempt, NewPassword));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        // And nothing was written: the account still cannot log in with the password the caller tried to set.
        using var anon = factory.AnonymousClient();
        var login = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest("alice", NewPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
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
        var response = await client.PostAsJsonAsync("/api/auth/set-password", new
        {
            currentPassword = ApiFactory.DefaultPassword,
            newPassword = NewPassword,
            userId = bobId,
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        // Alice's password changed...
        var aliceNewLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", NewPassword));
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
            "/api/auth/set-password", new AuthSetPasswordRequest(ApiFactory.DefaultPassword, ""));

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
            "/api/auth/set-password",
            new AuthSetPasswordRequest(ApiFactory.DefaultPassword, NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Admin set-password-for-user --------------------------------------------------------------------

    /// <summary>The admin route takes NO current password — an admin performing a reset does not know it.
    /// That asymmetry with the self route is deliberate, and this is the test that pins it: the admin's own
    /// password is never supplied, and the target's old one is never supplied either.</summary>
    [Fact]
    public async Task Admin_can_set_another_users_password_without_knowing_the_old_one()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest(NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var anon = factory.AnonymousClient();

        var oldLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("target", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("target", NewPassword));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    /// <summary>The admin reset is the ONLY way out of the NULL-hash state (self-service cannot be, since
    /// there is no current password to prove — see the self-route test above). A user with no hash cannot log
    /// in with anything beforehand, and only with the new password afterwards.</summary>
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
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest(NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // After: only the new password works.
        var wrongAfter = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("neverset", "anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAfter.StatusCode);

        var rightAfter = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("neverset", NewPassword));
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
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest(NewPassword));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And the target's password is untouched.
        using var anon = factory.AnonymousClient();
        var stillOld = await anon.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("target", ApiFactory.DefaultPassword));
        Assert.Equal(HttpStatusCode.OK, stillOld.StatusCode);
    }

    [Fact]
    public async Task Anonymous_gets_401_on_admin_set_password()
    {
        using var factory = new ApiFactory();
        var targetId = await factory.SeedUserAsync("target", ApiFactory.DefaultPassword);
        using var client = factory.AnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/auth/users/{targetId}/set-password", new AuthAdminSetPasswordRequest(NewPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_set_password_on_an_unknown_user_returns_404()
    {
        using var factory = new ApiFactory();
        using var admin = await factory.AdminClientAsync();

        var response = await admin.PostAsJsonAsync(
            "/api/auth/users/999999/set-password", new AuthAdminSetPasswordRequest(NewPassword));

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
