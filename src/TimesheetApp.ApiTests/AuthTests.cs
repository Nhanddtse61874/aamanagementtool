using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TimesheetApp.Api.Contracts;
using Xunit;

namespace TimesheetApp.ApiTests;

public sealed class AuthTests
{
    [Fact]
    public async Task A_user_with_the_right_password_logs_in()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);

        using var client = factory.AnonymousClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(userId, body!.Id);
        Assert.Equal("alice", body.Username);
        Assert.False(body.IsAdmin);
    }

    [Fact]
    public async Task A_wrong_password_is_rejected()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);

        using var client = factory.AnonymousClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", "not-the-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_username_is_rejected()
    {
        using var factory = new ApiFactory();

        using var client = factory.AnonymousClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("nobody", ApiFactory.DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A soft-deleted user who still has a valid password must not be able to log in.
    /// <c>GetCredentialsAsync</c> deliberately does NOT filter on <c>is_active</c> — it projects it — so
    /// rejecting the deactivated user is the CALLER's job, and omitting it is silent.</summary>
    [Fact]
    public async Task A_deactivated_user_cannot_log_in_even_with_the_right_password()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("gone", ApiFactory.DefaultPassword);
        await factory.SetUserActiveAsync(userId, isActive: false);

        using var client = factory.AnonymousClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("gone", ApiFactory.DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>THE AUTHENTICATION-BYPASS SHAPE. A NULL <c>password_hash</c> means "has never had a password
    /// set" — the state EVERY user is in on a freshly migrated v10 database. It must mean "cannot log in",
    /// never "any password matches". Empty string is included on purpose: it is what an empty form field
    /// sends, and it is the exact input a naive <c>hash is null =&gt; allow</c> would wave through.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("anything")]
    [InlineData(ApiFactory.DefaultPassword)]
    public async Task A_user_with_no_password_hash_cannot_log_in_with_any_password(string attempt)
    {
        using var factory = new ApiFactory();
        await factory.SeedUserWithoutPasswordAsync("neverset");

        using var client = factory.AnonymousClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("neverset", attempt));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>"Stay logged in" requires a PERSISTENT cookie — one with an expiry. A session cookie sends
    /// no <c>expires=</c> and dies with the browser.</summary>
    [Fact]
    public async Task The_auth_cookie_is_persistent()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("alice", ApiFactory.DefaultPassword));

        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        Assert.Contains("TimesheetApp.Auth=", setCookie);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>OT-1. Cookie auth on .NET 8 does NOT 401 by default — it 302s to /Account/Login, a route
    /// that does not exist here, so the SPA sees 404 and can never tell "logged out" from "broken".
    /// Microsoft changed this default only in .NET 10.</summary>
    [Fact]
    public async Task A_logged_out_request_gets_401_not_302_and_not_404()
    {
        using var factory = new ApiFactory();

        // Redirects must not be followed, or a 302 would be laundered into the 404 it points at.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>OT-4. <c>RequireClaim</c> compares with <c>StringComparer.Ordinal</c>, so a claim written
    /// from a bool renders "True" and the policy NEVER matches — a real admin gets a silent 403 with nothing
    /// logged. Asserting only that a non-admin is denied would pass even then; the load-bearing half is that
    /// a REAL ADMIN IS ADMITTED.</summary>
    [Fact]
    public async Task The_admin_policy_admits_a_real_admin()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("boss", ApiFactory.DefaultPassword, isAdmin: true);

        using var client = await factory.ClientAsync("boss");
        var response = await client.GetAsync("/probe/admin-only");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_policy_denies_a_non_admin_with_403()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("peon", ApiFactory.DefaultPassword);

        using var client = await factory.ClientAsync("peon");
        var response = await client.GetAsync("/probe/admin-only");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>OT-2, and the bug that is INVISIBLE UNTIL PRODUCTION.
    ///
    /// <para>The auth cookie is encrypted with a Data Protection key ring. Unconfigured, that ring lives in
    /// MEMORY — so every process restart silently logs everyone out. The host is a workstation: it reboots,
    /// it updates, it recycles. Mass logouts roughly daily, with NOTHING in the logs.</para>
    ///
    /// <para>Factory A and factory B are two independent hosts — separate DI containers, separate
    /// DataProtection providers, separate in-memory key caches — over ONE key-ring folder and ONE database.
    /// B can only decrypt A's cookie if the keys were actually written to disk and read back.</para></summary>
    [Fact]
    public async Task The_data_protection_key_ring_survives_a_restart()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            string cookie;
            using (var first = new ApiFactory(root))
            {
                await first.SeedUserAsync("erin", ApiFactory.DefaultPassword);
                cookie = await first.LoginCookieAsync("erin");
            }

            // A brand-new host over the same key ring: the "restart".
            using var second = new ApiFactory(root);
            using var client = second.CreateClient(
                new WebApplicationFactoryClientOptions { HandleCookies = false });

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Add("Cookie", cookie);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>The negative control for the test above. Without it, "the cookie still works" could just mean
    /// the cookie is not encrypted at all, or that the two factories somehow shared a provider — and the
    /// restart test would pass while proving nothing. A DIFFERENT key ring MUST reject the same cookie.</summary>
    [Fact]
    public async Task A_cookie_from_a_different_key_ring_is_rejected()
    {
        var rootA = ApiFactory.NewRoot();
        var rootB = ApiFactory.NewRoot();
        try
        {
            string cookie;
            using (var first = new ApiFactory(rootA))
            {
                await first.SeedUserAsync("erin", ApiFactory.DefaultPassword);
                cookie = await first.LoginCookieAsync("erin");
            }

            // Same user, same password, DIFFERENT key ring (and a different database file).
            using var other = new ApiFactory(rootB);
            await other.SeedUserAsync("erin", ApiFactory.DefaultPassword);

            using var client = other.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
                AllowAutoRedirect = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Add("Cookie", cookie);
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            Cleanup(rootA);
            Cleanup(rootB);
        }
    }

    [Fact]
    public async Task Logout_clears_the_cookie_and_the_next_request_is_401()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);

        using var client = await factory.ClientAsync("alice");
        (await client.GetAsync("/api/me")).EnsureSuccessStatusCode();

        var logout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var after = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>A cookie naming a user who was renamed or soft-deleted AFTER it was issued must not be
    /// honoured: <c>ResolveAsync</c> returns NeedsSelection, <c>Current</c> is null, and proceeding would
    /// write timesheet rows and standup entries on behalf of a user who no longer exists.</summary>
    [Fact]
    public async Task A_cookie_for_a_user_who_no_longer_resolves_is_401()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("ghost", ApiFactory.DefaultPassword);

        using var client = await factory.ClientAsync("ghost");
        (await client.GetAsync("/api/me")).EnsureSuccessStatusCode();

        // The username the cookie names no longer exists.
        using (var db = factory.OpenDb())
        {
            await Dapper.SqlMapper.ExecuteAsync(db,
                "UPDATE Users SET username = 'renamed' WHERE id = @id;", new { id = userId });
        }

        var after = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    private static void Cleanup(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Temp file occasionally held briefly on Windows; safe to leave for the OS to reap.
        }
    }
}
