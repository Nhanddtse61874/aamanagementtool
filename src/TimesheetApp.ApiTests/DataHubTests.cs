using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>End-to-end <c>DataHub</c> tests: a REAL <see cref="HubConnection"/> over the
/// <c>WebApplicationFactory</c> test server, not <see cref="RecordingChangeNotifier"/>.
///
/// <para><b>Why this file exists alongside <see cref="ChangeNotifierContractTests"/>.</b> That file proves
/// an endpoint calls <c>IChangeNotifier</c> with the right arguments. It proves NOTHING about whether a
/// second browser actually receives a message: <c>RecordingChangeNotifier</c> replaces the hub entirely.
/// Only a live <see cref="HubConnection"/> that actually joined a group can prove group membership, the
/// reconnect-rejoin behaviour, and the caller-exclusion are real.</para>
///
/// <para><b>Transport is pinned to <see cref="HttpTransportType.LongPolling"/>.</b> The default negotiated
/// transport is WebSockets, which opens a real socket rather than going through
/// <c>HttpConnectionOptions.HttpMessageHandlerFactory</c> — against the in-memory <c>TestServer</c> that
/// hangs/fails with no useful error. LongPolling is plain HTTP requests and tunnels entirely through the
/// supplied handler, exactly like every other test in this suite.</para>
///
/// <para><b>The auth cookie is set as a raw <c>Cookie</c> header, not <c>HttpConnectionOptions.Cookies</c>.</b>
/// SignalR's client applies a <c>CookieContainer</c> to the internal <c>HttpClientHandler</c> it builds and
/// then offers to <c>HttpMessageHandlerFactory</c> as the "inner" handler for the factory to wrap. Our
/// factory ignores that inner handler and substitutes <c>factory.Server.CreateHandler()</c> outright, so
/// anything written onto the discarded handler's cookie container never reaches an actual request — a trap
/// found only by running this, not by reading the option's name. Setting the header directly bypasses it.</para>
///
/// <para><b>Never <c>:memory:</c>.</b> <see cref="SignalRTestFactory"/> already guarantees this; restated
/// because a two-connection test is exactly the shape a <c>:memory:</c> database would silently defeat.</para>
///
/// <para><b>Uses <see cref="SignalRTestFactory"/>, NOT <see cref="ApiFactory"/>.</b> <c>ApiFactory</c>
/// unconditionally replaces <c>IChangeNotifier</c> with <c>RecordingChangeNotifier</c> for every test built
/// on it, which makes the REAL <c>SignalRChangeNotifier</c> unreachable through an HTTP mutation — confirmed
/// by running a probe, not assumed. See <see cref="SignalRTestFactory"/>'s own doc for the full story.</para></summary>
public sealed class DataHubTests
{
    private const string HubPath = "hubs/data";
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SilenceGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>Two team members; A's mutation reaches B and not A — and B keeps receiving after being
    /// forced to reconnect. Combined into one test because the second half only means something once the
    /// first half is established: prove delivery, force a reconnect, prove it again. That is the shape of
    /// the bug that otherwise ships clean and dies quietly after the first Wi-Fi blip.</summary>
    [Fact]
    public async Task A_mutation_reaches_the_other_team_member_not_the_caller_and_survives_a_reconnect()
    {
        using var factory = new SignalRTestFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var teamId = await factory.SeedTeamAsync("Team A", aliceId, bobId);
        var backlogId = await factory.SeedBacklogAsync(teamId, "REQ-950");
        var taskId = await factory.SeedTaskAsync(backlogId, "Log against me");

        await using var aliceHub = await ConnectAsync(factory, "alice");
        await using var bobHub = await ConnectAsync(factory, "bob");

        var aliceInbox = ListenFor(aliceHub);
        var bobInbox = ListenFor(bobHub);

        // Alice's OWN connection id must ride on the HTTP request that performs her mutation — that is the
        // only way the server knows which live connection to exclude (exceptConnectionId).
        using var aliceHttp = await factory.ClientAsync("alice", connectionId: aliceHub.ConnectionId);

        var save1 = await aliceHttp.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskId, Weekday(), 3m, ExpectedVersion: null));
        Assert.Equal(HttpStatusCode.OK, save1.StatusCode);
        var saved1 = await save1.Content.ReadFromJsonAsync<SavedBody>();

        var received1 = await ReadWithTimeoutAsync(bobInbox.Reader);
        Assert.Equal(DataKind.Logs, received1.Kind);
        Assert.Equal(teamId, received1.TeamId);

        // The echo-suppression half: give a (wrongly sent) echo time to arrive, then assert it never did.
        await AssertNoMessageWithinAsync(aliceInbox.Reader);

        // ---- Force B to reconnect. A fresh connection gets a fresh ConnectionId and starts in zero
        // groups; if DataHub relied on membership surviving a reconnect instead of rejoining in
        // OnConnectedAsync, this is exactly the point cross-user sync would go silently dark. ----
        var oldConnectionId = bobHub.ConnectionId;
        await bobHub.StopAsync();
        await bobHub.StartAsync();
        Assert.NotNull(bobHub.ConnectionId);
        Assert.NotEqual(oldConnectionId, bobHub.ConnectionId);

        // Same HubConnection object, same registered handler (SignalR handlers survive a reconnect; they
        // are attached to the connection object, not the underlying transport) — so bobInbox itself is
        // reused, proving THIS handler received a message after the reconnect, not a freshly wired one.
        var save2 = await aliceHttp.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskId, Weekday(), 4m, ExpectedVersion: saved1!.RowVersion));
        Assert.Equal(HttpStatusCode.OK, save2.StatusCode);

        var received2 = await ReadWithTimeoutAsync(bobInbox.Reader);
        Assert.Equal(DataKind.Logs, received2.Kind);
        Assert.Equal(teamId, received2.TeamId);
    }

    /// <summary>A global entity (no team column) must reach EVERY connected client, regardless of team
    /// membership, and still exclude the caller. Alice, Bob and the admin are deliberately spread across
    /// different teams (admin in none at all) so a broadcast that accidentally went through a group instead
    /// of <c>AllExcept</c> would fail this the same way a missing broadcast would.</summary>
    [Fact]
    public async Task A_global_change_reaches_every_connected_client_except_the_caller()
    {
        using var factory = new SignalRTestFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        await factory.SeedUserAsync(ApiFactory.AdminUserName, ApiFactory.DefaultPassword, isAdmin: true);

        await factory.SeedTeamAsync("Team A", aliceId);
        await factory.SeedTeamAsync("Team B", bobId);
        // admin joins no team at all — a global broadcast must still reach them.

        await using var aliceHub = await ConnectAsync(factory, "alice");
        await using var bobHub = await ConnectAsync(factory, "bob");
        await using var adminHub = await ConnectAsync(factory, ApiFactory.AdminUserName);

        var aliceInbox = ListenFor(aliceHub);
        var bobInbox = ListenFor(bobHub);
        var adminInbox = ListenFor(adminHub);

        using var adminHttp = await factory.ClientAsync(
            ApiFactory.AdminUserName, ApiFactory.DefaultPassword, connectionId: adminHub.ConnectionId);

        var response = await adminHttp.PostAsJsonAsync("/api/tags",
            new SettingsTagCreateRequest("Urgent", "flame", "#ff0000"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var toAlice = await ReadWithTimeoutAsync(aliceInbox.Reader);
        Assert.Equal(DataKind.Tags, toAlice.Kind);
        Assert.Equal(0, toAlice.TeamId);

        var toBob = await ReadWithTimeoutAsync(bobInbox.Reader);
        Assert.Equal(DataKind.Tags, toBob.Kind);
        Assert.Equal(0, toBob.TeamId);

        // AllExcept, not All: the admin made the change and must not receive their own broadcast either.
        await AssertNoMessageWithinAsync(adminInbox.Reader);
    }

    /// <summary>R6 for the push channel: a client must never be in the group for a team it does not belong
    /// to. Bob is in Team B only; Alice's change to Team A must not reach him. A positive control
    /// (Carol — Bob's real teammate — changing Team B) proves the silence above is membership, not a dead
    /// connection or a broken harness.</summary>
    [Fact]
    public async Task A_client_is_never_grouped_into_a_team_it_does_not_belong_to()
    {
        using var factory = new SignalRTestFactory();
        var aliceId = await factory.SeedUserAsync("alice", ApiFactory.DefaultPassword);
        var bobId = await factory.SeedUserAsync("bob", ApiFactory.DefaultPassword);
        var carolId = await factory.SeedUserAsync("carol", ApiFactory.DefaultPassword);

        var teamA = await factory.SeedTeamAsync("Team A", aliceId);
        var teamB = await factory.SeedTeamAsync("Team B", bobId, carolId);   // bob is NOT in Team A

        var backlogA = await factory.SeedBacklogAsync(teamA, "REQ-960");
        var taskA = await factory.SeedTaskAsync(backlogA, "Team A task");

        var backlogB = await factory.SeedBacklogAsync(teamB, "REQ-961");
        var taskB = await factory.SeedTaskAsync(backlogB, "Team B task");

        await using var bobHub = await ConnectAsync(factory, "bob");
        var bobInbox = ListenFor(bobHub);

        // Alice mutates TEAM A, which bob does not belong to.
        using var aliceHttp = await factory.ClientAsync("alice");
        var aliceSave = await aliceHttp.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskA, Weekday(), 2m, ExpectedVersion: null));
        Assert.Equal(HttpStatusCode.OK, aliceSave.StatusCode);

        await AssertNoMessageWithinAsync(bobInbox.Reader);

        // Positive control: Carol (Bob's real teammate) mutates TEAM B, and Bob DOES receive it.
        using var carolHttp = await factory.ClientAsync("carol");
        var carolSave = await carolHttp.PutAsJsonAsync("/api/timesheet/cell",
            new TimesheetSaveCellRequest(taskB, Weekday(), 2m, ExpectedVersion: null));
        Assert.Equal(HttpStatusCode.OK, carolSave.StatusCode);

        var received = await ReadWithTimeoutAsync(bobInbox.Reader);
        Assert.Equal(DataKind.Logs, received.Kind);
        Assert.Equal(teamB, received.TeamId);
    }

    /// <summary><c>[Authorize]</c> on <c>DataHub</c> plus the API's <c>FallbackPolicy</c> both require an
    /// authenticated principal. A connection presenting no cookie must fail to start, never silently
    /// connect and sit in zero groups looking exactly like a member with nothing to say.</summary>
    [Fact]
    public async Task An_anonymous_connection_cannot_start_the_hub()
    {
        using var factory = new SignalRTestFactory();

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HubPath), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    // ===== helpers =======================================================================================

    private static async Task<HubConnection> ConnectAsync(SignalRTestFactory factory, string userName)
    {
        var rawCookie = await factory.LoginCookieAsync(userName);

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HubPath), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers = new Dictionary<string, string> { ["Cookie"] = rawCookie };
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>Subscribes to the <c>DataChanged</c> hub method and hands every call to an unbounded
    /// channel, so a test can await the next one (or prove none arrived) without racing a bare field.</summary>
    private static Channel<(DataKind Kind, int TeamId)> ListenFor(HubConnection connection)
    {
        var channel = Channel.CreateUnbounded<(DataKind Kind, int TeamId)>();
        connection.On<DataKind, int>(
            SignalRChangeNotifierClientMethod, (kind, teamId) => channel.Writer.TryWrite((kind, teamId)));
        return channel;
    }

    // Mirrors TimesheetApp.Api.Infrastructure.SignalRChangeNotifier.ClientMethod. Not referenced directly
    // to keep this test file decoupled from an internal implementation constant; a rename on either side
    // that is not mirrored on the other fails every test in this file loudly, which is the point.
    private const string SignalRChangeNotifierClientMethod = "DataChanged";

    private static async Task<(DataKind Kind, int TeamId)> ReadWithTimeoutAsync(
        ChannelReader<(DataKind Kind, int TeamId)> reader)
    {
        using var cts = new CancellationTokenSource(ReceiveTimeout);
        try
        {
            return await reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"No DataChanged message received within {ReceiveTimeout.TotalSeconds}s.");
        }
    }

    private static async Task AssertNoMessageWithinAsync(ChannelReader<(DataKind Kind, int TeamId)> reader)
    {
        using var cts = new CancellationTokenSource(SilenceGrace);
        try
        {
            var gotOne = await reader.WaitToReadAsync(cts.Token);
            Assert.False(gotOne, "Expected no DataChanged message, but one had already arrived.");
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting for a message to become available -- exactly what "none arrived" means.
        }
    }

    /// <summary>TimeLogService rejects a weekend outright, so a cell test that happens to run on a
    /// Saturday would fail for a reason that has nothing to do with SignalR.</summary>
    private static DateOnly Weekday()
    {
        var day = DateOnly.FromDateTime(DateTime.Today);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            day = day.AddDays(-1);
        return day;
    }
}
