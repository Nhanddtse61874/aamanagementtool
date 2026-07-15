using System.Net;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>The single-process local deploy (P2): the API serves the built Angular UI from wwwroot and
/// falls back to index.html for client-side routes. These tests pin the ONE property that fallback wiring
/// gets wrong most often — that <c>MapFallbackToFile</c> must NOT swallow the real API, the hub, or health.
///
/// <para>The test host has NO wwwroot (production copies it in via deploy-local.bat), so the fallback file
/// is simply not found and an unknown path 404s. That is fine: the load-bearing assertion is that <c>/api/*</c>
/// still routes to the API and still enforces auth, i.e. the fallback did not shadow it.</para></summary>
public sealed class SpaFallbackTests
{
    /// <summary>The most important one: a real, auth-required API route must still reach the API and 401 an
    /// anonymous caller. If the SPA fallback shadowed it, this would be 200 (index.html) or 404, never 401.</summary>
    [Fact]
    public async Task Api_route_still_401s_anonymous_and_is_not_shadowed_by_the_spa_fallback()
    {
        using var factory = new ApiFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Health is anonymous and mapped, so it must match before the fallback and return 200 — proof
    /// the fallback does not eat mapped non-/api endpoints either.</summary>
    [Fact]
    public async Task Health_still_routes_past_the_spa_fallback()
    {
        using var factory = new ApiFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>An unknown, non-API path is what the fallback exists for. With no wwwroot in the test host the
    /// file is not found, so it 404s. The point is that it is NOT 401: the fallback is AllowAnonymous, so a
    /// client-side route never demands a cookie (a logged-out user can always reach the login shell).</summary>
    [Fact]
    public async Task An_unknown_non_api_path_reaches_the_anonymous_fallback_and_404s_without_wwwroot()
    {
        using var factory = new ApiFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync("/some/client-side/route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // Specifically NOT 401 — that would mean the fallback is behind the FallbackPolicy and a logged-out
        // user could not load the app shell.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
