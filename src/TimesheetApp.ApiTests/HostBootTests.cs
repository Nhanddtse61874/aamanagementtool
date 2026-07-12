using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>The tests that kill this milestone's highest risks, as early and as loudly as possible.</summary>
public sealed class HostBootTests
{
    /// <summary>THE test. The host is built with <c>ValidateScopes</c> AND <c>ValidateOnBuild</c> on, so a
    /// captive dependency — "Cannot consume scoped service 'ICurrentTeamService' from singleton
    /// 'ITimeLogService'" — fails HERE, in one named test with the offending pair in the message, instead of
    /// failing all ~40 API tests with a message that points somewhere else entirely.</summary>
    [Fact]
    public void Host_builds_with_scope_validation_on()
    {
        using var factory = new ApiFactory();

        // Touching Services forces the host to build, which is where ValidateOnBuild runs.
        var provider = factory.Services;

        Assert.NotNull(provider);
    }

    /// <summary>Proves the per-user services really are per-request, not merely intended to be.
    ///
    /// <para>The WPF composition root registers all 43 services as singletons — correct on a desktop, where
    /// one process serves one user, and a cross-user data leak on a server. If any of these regressed to
    /// Singleton, it would resolve happily from the root and one user's identity would serve every other
    /// user's request. Under <c>ValidateScopes</c> a scoped service CANNOT be resolved from the root
    /// provider, so this asserts the lifetime directly rather than by inspection.</para></summary>
    [Fact]
    public void Per_user_services_are_scoped_and_cannot_be_captured_by_a_singleton()
    {
        using var factory = new ApiFactory();
        var root = factory.Services;

        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<ICurrentUserService>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<ICurrentTeamService>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<IClientContext>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<ITimeLogService>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<IStandupService>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<IBacklogContinuationService>());

        // ...and every one of them resolves inside a scope.
        using var scope = root.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICurrentUserService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICurrentTeamService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IClientContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITimeLogService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IStandupService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IBacklogContinuationService>());
    }

    /// <summary>The connection factory's constructor defaults to <c>SqliteProfile.Desktop</c>, so a plain
    /// <c>AddSingleton&lt;IConnectionFactory, SqliteConnectionFactory&gt;()</c> would SILENTLY give the API
    /// journal_mode=DELETE (readers block writers) and no DefaultTimeout (a blocked writer takes ~34s to
    /// fail, not 5). Asserted from the API's OWN container — not a hand-built factory — because the
    /// registration is the thing that can be wrong.</summary>
    [Fact]
    public void Sqlite_runs_the_server_profile_wal_from_the_apis_own_container()
    {
        using var factory = new ApiFactory();
        using var connection = factory.OpenDb();

        var journalMode = connection.ExecuteScalar<string>("PRAGMA journal_mode;");

        Assert.Equal("wal", journalMode);
    }

    /// <summary>A health check that requires a login is not a health check. The FallbackPolicy would 401 it
    /// without an explicit AllowAnonymous.</summary>
    [Fact]
    public async Task Health_is_reachable_anonymously()
    {
        using var factory = new ApiFactory();
        using var client = factory.AnonymousClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The per-request initializer, end to end.
    ///
    /// <para>Nothing calls <c>ICurrentUserService.ResolveAsync()</c> or
    /// <c>ICurrentTeamService.InitializeAsync(userId)</c> for you. Without ClientContextFilter,
    /// <c>ActiveTeamId</c> stays 0 — and <c>GetActiveForTimesheetAsync(0)</c> returns no tasks — so every
    /// timesheet and standup endpoint would return EMPTY, and <c>StandupService.AddEntryAsync</c> would
    /// write <c>TeamId: 0</c>. A non-zero ActiveTeamId and a non-empty MemberTeamIds here are the proof
    /// that both fired.</para></summary>
    [Fact]
    public async Task Authenticated_request_resolves_the_user_their_active_team_and_their_member_teams()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", userId);

        using var client = await factory.ClientAsync("alice");
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.Equal(userId, me!.Id);
        // MeResponse.Name is the DISPLAY name ("Alice Nguyen"), not the login username ("alice"): it comes
        // from IClientContext.UserName, which ClientContextFilter populates from User.Name.
        Assert.Equal(ApiFactory.DisplayNameFor("alice"), me.Name);
        Assert.False(me.IsAdmin);

        // The AUTHORIZATION BOUND was loaded (from ITeamRepository.GetTeamIdsForUserAsync).
        Assert.Equal(new[] { teamId }, me.MemberTeamIds);

        // The WORKING SCOPE resolved. 0 here would mean every team-scoped endpoint silently returns empty.
        Assert.NotEqual(0, me.ActiveTeamId);
        Assert.Equal(teamId, me.ActiveTeamId);
    }

    /// <summary>A user who is in no team is a legitimate state (newly created, not yet assigned), and it
    /// must degrade to "sees nothing", never to "sees everything".</summary>
    [Fact]
    public async Task A_user_in_no_team_has_an_empty_bound_and_a_zero_active_team()
    {
        using var factory = new ApiFactory();
        await factory.SeedUserAsync("loner", ApiFactory.DefaultPassword);

        using var client = await factory.ClientAsync("loner");
        var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

        Assert.NotNull(me);
        Assert.Empty(me!.MemberTeamIds);
        Assert.Equal(0, me.ActiveTeamId);
    }

    /// <summary>The X-Connection-Id header is the ONLY way the caller's SignalR connection id reaches the
    /// server (it is not on HttpContext), and Wave 3 needs it to keep a mutation from echoing back to the
    /// user who made it. Read once, here, so four Wave-2 agents do not each invent their own reader.</summary>
    [Fact]
    public async Task The_signalr_connection_id_is_read_from_the_x_connection_id_header()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", userId);

        using var client = await factory.ClientAsync("carol");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe/context");
        request.Headers.Add("X-Connection-Id", "conn-abc-123");
        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var ctx = await response.Content.ReadFromJsonAsync<ProbeContext>();
        Assert.Equal("conn-abc-123", ctx!.ConnectionId);
    }

    /// <summary>No header => no connection id. Wave 3 must treat null as "this caller has no live hub
    /// connection", never as "exclude nobody".</summary>
    [Fact]
    public async Task The_connection_id_is_null_when_the_client_sends_no_header()
    {
        using var factory = new ApiFactory();
        var userId = await factory.SeedUserAsync("dave", ApiFactory.DefaultPassword);
        await factory.SeedTeamAsync("Team A", userId);

        using var client = await factory.ClientAsync("dave");
        var ctx = await client.GetFromJsonAsync<ProbeContext>("/probe/context");

        Assert.Null(ctx!.ConnectionId);
    }
}
