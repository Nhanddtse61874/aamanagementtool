using System.Data;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Data;

namespace TimesheetApp.ApiTests;

/// <summary>A second <c>WebApplicationFactory&lt;Program&gt;</c>, used ONLY by <see cref="DataHubTests"/>,
/// and deliberately NOT <see cref="ApiFactory"/>.
///
/// <para><b>Why it has to exist.</b> <see cref="ApiFactory"/>'s <c>ConfigureTestServices</c>
/// unconditionally runs <c>services.Replace(ServiceDescriptor.Singleton&lt;IChangeNotifier&gt;(Notifier))</c>
/// — by design, so the endpoint-to-notifier CONTRACT is observable (see <see cref="RecordingChangeNotifier"/>'s
/// own doc). That same line makes the REAL <c>SignalRChangeNotifier</c> structurally unreachable through any
/// test built on <c>ApiFactory</c>: confirmed by running a probe before writing these tests — an HTTP
/// mutation through an <c>ApiFactory</c> host resolves <c>RecordingChangeNotifier</c> every time (proven via
/// <c>factory.Services.GetRequiredService&lt;IChangeNotifier&gt;().GetType()</c>), so a live
/// <c>HubConnection</c> attached to that same host receives NOTHING no matter what <c>DataHub</c> does. That
/// is not a DataHub bug; it is the shared fixture doing exactly what Wave 1 built it to do, for a purpose
/// this wave's tests are not that purpose.</para>
///
/// <para><c>ApiFactory.cs</c> is Wave-1-owned and out of THIS wave's scope to change (and is <c>sealed</c>,
/// so it cannot be subclassed to override just the one registration) — hence a second, minimal fixture
/// instead. It mirrors only what <see cref="DataHubTests"/> needs from <c>ApiFactory</c>: the same
/// temp-root / DbPath / KeyRingPath wiring, the same direct-SQL seeds, and a login-cookie helper — reusing
/// <see cref="ApiFactory.DisplayNameFor"/> and <see cref="AuthSetup.HashPassword"/> rather than
/// re-deriving them, so the two fixtures cannot drift on what a seeded user looks like.</para>
///
/// <para><b>Never <c>:memory:</c></b> — same reason as <c>ApiFactory</c>: each connection would get its own
/// database, silently defeating every multi-client test in <see cref="DataHubTests"/>.</para></summary>
public sealed class SignalRTestFactory : WebApplicationFactory<Program>
{
    public const string DefaultPassword = ApiFactory.DefaultPassword;

    private readonly string _root;

    public string DbPath => Path.Combine(_root, "timesheet.db");
    public string KeyRingPath => Path.Combine(_root, "keys");
    public string ConfigPath => Path.Combine(_root, "appsettings.json");

    public SignalRTestFactory()
    {
        _root = Path.Combine(Path.GetTempPath(), "tsapi-signalr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TimesheetApp:ConfigPath", ConfigPath);
        builder.UseSetting("TimesheetApp:DbPath", DbPath);
        builder.UseSetting("TimesheetApp:KeyRingPath", KeyRingPath);
        builder.UseEnvironment("Testing");

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        // Deliberately NO ConfigureTestServices IChangeNotifier replacement. Leaving Program.cs's own
        // AddSingleton<IChangeNotifier, SignalRChangeNotifier>() registration in place is the entire
        // reason this fixture exists instead of ApiFactory.
    }

    public IDbConnection OpenDb() => Services.GetRequiredService<IConnectionFactory>().Create();

    public async Task<int> SeedUserAsync(string userName, string password, bool isAdmin = false)
    {
        using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, username, is_active, is_admin, password_hash)
              VALUES(@name, @userName, 1, @admin, @hash);
              SELECT last_insert_rowid();",
            new
            {
                name = ApiFactory.DisplayNameFor(userName),
                userName,
                admin = isAdmin ? 1 : 0,
                hash = AuthSetup.HashPassword(password),
            });
    }

    public async Task<int> SeedTeamAsync(string name, params int[] memberUserIds)
    {
        using var c = OpenDb();
        var teamId = await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Teams(name, is_active, created_at) VALUES(@n, 1, @now);
              SELECT last_insert_rowid();",
            new { n = name, now = Stamp() });

        foreach (var userId in memberUserIds)
        {
            await c.ExecuteAsync(
                "INSERT OR IGNORE INTO UserTeams(user_id, team_id) VALUES(@u, @t);",
                new { u = userId, t = teamId });
        }

        return teamId;
    }

    public async Task<int> SeedBacklogAsync(int teamId, string code)
    {
        using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Backlogs(backlog_code, project, created_at, team_id)
              VALUES(@code, 'ARCS', @now, @teamId);
              SELECT last_insert_rowid();",
            new { code, now = Stamp(), teamId });
    }

    public async Task<int> SeedTaskAsync(int backlogId, string name)
    {
        using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Tasks(backlog_id, task_name, order_index, is_active)
              VALUES(@backlogId, @name, 0, 1);
              SELECT last_insert_rowid();",
            new { backlogId, name });
    }

    /// <summary>A client logged in as <paramref name="userName"/>, with the auth cookie held in the
    /// handler's cookie container. <paramref name="connectionId"/> mirrors <c>ApiFactory.ClientAsync</c>:
    /// it sets <c>X-Connection-Id</c> on every request, the only route by which a mutation's
    /// <c>exceptConnectionId</c> reaches the server.</summary>
    public async Task<HttpClient> ClientAsync(
        string userName, string password = DefaultPassword, string? connectionId = null)
    {
        var client = CreateClient();
        if (connectionId is not null)
            client.DefaultRequestHeaders.Add("X-Connection-Id", connectionId);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login for '{userName}' failed: {(int)response.StatusCode}.");
        return client;
    }

    /// <summary>Logs in and returns the raw <c>Set-Cookie</c> value, for attaching to a
    /// <c>HubConnectionBuilder</c> as a literal <c>Cookie</c> header (see <see cref="DataHubTests"/> for
    /// why that, and not <c>HttpConnectionOptions.Cookies</c>, is what actually works against a
    /// substituted <c>HttpMessageHandlerFactory</c>).</summary>
    public async Task<string> LoginCookieAsync(string userName, string password = DefaultPassword)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login for '{userName}' failed: {(int)response.StatusCode}.");

        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0];   // "TimesheetApp.Auth=<value>"
    }

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp file occasionally held briefly on Windows; safe to leave for the OS to reap.
        }
    }
}
