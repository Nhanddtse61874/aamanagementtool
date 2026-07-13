using System.Data;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.ApiTests;

/// <summary>THE shared API test fixture. Every Wave-2 endpoint test builds on this.
///
/// <para><b>Why it is frozen here rather than grown per test file.</b> <c>FallbackPolicy = DefaultPolicy</c>
/// means EVERY endpoint requires authentication, so every endpoint test needs a logged-in client and seeded
/// data. Left to four parallel agents, four <c>SeedTeamAsync</c>es get written — which is the
/// <c>Models/Entities.cs</c> failure verbatim, one layer up.</para>
///
/// <para><b>Never <c>:memory:</c>.</b> Each SQLite in-memory connection gets its OWN database, so any
/// two-connection test — i.e. every concurrency test in this milestone — would pass while asserting
/// nothing. A temp file, exactly like the 656 existing tests.</para>
///
/// <para><b><c>TimesheetApp.Tests/Data/TestDb.cs</c> cannot be reused:</b> it targets
/// <c>net8.0-windows</c>, and it seeds no <c>password_hash</c>, so nobody it creates can log in.</para></summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>The password every seeded user gets unless told otherwise.</summary>
    public const string DefaultPassword = "Pa55w0rd!";

    /// <summary>The username <see cref="AdminClientAsync"/> seeds and logs in as.</summary>
    public const string AdminUserName = "admin";

    private readonly bool _ownsRoot;

    public string Root { get; }
    public string DbPath => Path.Combine(Root, "timesheet.db");
    public string KeyRingPath => Path.Combine(Root, "keys");
    public string ConfigPath => Path.Combine(Root, "appsettings.json");

    /// <summary>Every log message the host emitted at Warning or above — including the one-time admin
    /// password, which exists nowhere else by design.</summary>
    public List<string> Logs { get; } = new();

    /// <summary>Every <c>IChangeNotifier.DataChangedAsync</c> call this host made. Replaces the
    /// <c>NoopChangeNotifier</c> the production composition root registers — without this the notify
    /// contract is unobservable in tests, not merely untested. See <see cref="RecordingChangeNotifier"/>.</summary>
    public RecordingChangeNotifier Notifier { get; } = new();

    public ApiFactory() : this(NewRoot(), ownsRoot: true) { }

    /// <summary>Two factories over ONE root = a process restart against the same database AND the same
    /// Data Protection key ring. That is the only way to prove the key ring actually survives a restart —
    /// the failure it guards against is invisible until production.</summary>
    public ApiFactory(string root) : this(root, ownsRoot: false) { }

    private ApiFactory(string root, bool ownsRoot)
    {
        Root = root;
        _ownsRoot = ownsRoot;
        Directory.CreateDirectory(Root);
    }

    public static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "tsapi-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("TimesheetApp:ConfigPath", ConfigPath);
        builder.UseSetting("TimesheetApp:DbPath", DbPath);
        builder.UseSetting("TimesheetApp:KeyRingPath", KeyRingPath);
        builder.UseEnvironment("Testing");

        // The host logs every request at Information by default, which buries the assertion that failed.
        // Warnings and errors still surface — including the one-time generated admin password.
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(new ListLoggerProvider(Logs));
        });

        // ConfigureTestServices runs AFTER Program.cs has populated the service collection, so it is the
        // seam for both a test-only ADDITION (the probe routes) and a test-only REPLACEMENT (the notifier).
        builder.ConfigureTestServices(services =>
        {
            // Test-only probe routes on the real pipeline. See ProbeEndpointsFilter.
            services.AddSingleton<IStartupFilter, ProbeEndpointsFilter>();

            // Replace(), not Add(): "last registration wins" is true for GetRequiredService but leaves the
            // Noop in the collection, so anything resolving IEnumerable<IChangeNotifier> would still see it.
            // Replace states the intent and leaves exactly one.
            services.Replace(ServiceDescriptor.Singleton<IChangeNotifier>(Notifier));
        });
    }

    /// <summary>A connection from the API's OWN container — same factory, same SqliteProfile.Server, same
    /// file the endpoints write to. Use it to arrange a competing write (for a 409) or to assert state.</summary>
    public IDbConnection OpenDb() => Services.GetRequiredService<IConnectionFactory>().Create();

    // ---- Seeds -------------------------------------------------------------------------------------

    // =================================================================================================
    // THE LOGIN USERNAME AND THE DISPLAY NAME ARE DIFFERENT STRINGS. THEY USED NOT TO BE.
    //
    // Until M8.3/W2.5 both seeds wrote `name` and `username` as the SAME string. That made a whole class
    // of bug STRUCTURALLY INVISIBLE: any code that reached for the display name where it needed the login
    // username (or vice versa) PASSED EVERY TEST IN THIS SUITE and failed only in production, where people
    // are called "Alice Nguyen" and log in as "alice". Concretely, the confusable pairs are:
    //
    //     ClaimTypes.Name              -> username   (CurrentUserService feeds it to GetByUsernameAsync)
    //     IClientContext.UserName      -> DISPLAY    (the changedByName on every audited write)
    //     IUserRepository.GetCredentialsAsync / GetByUsernameAsync keyed by username
    //     UserCredentials.Name, User.Name, LoginResponse.Name, MeResponse.Name -> DISPLAY
    //     User.WindowsUsername (property kept its old name), UserDto.Username  -> username
    //
    // Every one of those is now provably distinguishable: seed "alice" and the suite exercises a user
    // whose username is "alice" and whose display name is "Alice Nguyen". Swap the two anywhere and a
    // lookup returns null (401) or an assertion on the display name fails.
    //
    // The PARAMETER IS THE USERNAME, not the display name, and that direction is deliberate: every caller
    // already passes this same string to ClientAsync/LoginCookieAsync, which log in with it — so the
    // string was always semantically the username, and only the fixture pretended otherwise. Deriving the
    // display name from it (rather than the other way round) makes the fixture honest without silently
    // re-pointing ~60 existing login call sites at a value they never meant.
    // =================================================================================================

    /// <summary>The display name seeded for a given login <paramref name="userName"/>: "alice" logs in,
    /// "Alice Nguyen" is what shows up on an audit row and in <c>GET /api/me</c>. Deterministic, so a test
    /// can assert the display name WITHOUT hardcoding the derivation.</summary>
    public static string DisplayNameFor(string userName) =>
        char.ToUpperInvariant(userName[0]) + userName[1..] + " Nguyen";

    /// <summary>Seeds an active user who logs in as <paramref name="userName"/> and is DISPLAYED as
    /// <see cref="DisplayNameFor"/>(<paramref name="userName"/>), with a real PBKDF2 hash of
    /// <paramref name="password"/>. Returns the id.</summary>
    public async Task<int> SeedUserAsync(string userName, string password, bool isAdmin = false)
    {
        using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, username, is_active, is_admin, password_hash)
              VALUES(@name, @userName, 1, @admin, @hash);
              SELECT last_insert_rowid();",
            new
            {
                name = DisplayNameFor(userName),
                userName,
                admin = isAdmin ? 1 : 0,
                hash = AuthSetup.HashPassword(password),
            });
    }

    /// <summary>Seeds a user with a NULL <c>password_hash</c> — i.e. one who has never had a password set.
    /// This is the state EVERY user is in on a freshly migrated v10 database, and "no hash" must mean
    /// "cannot log in", never "any password matches".</summary>
    public async Task<int> SeedUserWithoutPasswordAsync(string userName, bool isAdmin = false)
    {
        using var c = OpenDb();
        return await c.ExecuteScalarAsync<int>(
            @"INSERT INTO Users(name, username, is_active, is_admin, password_hash)
              VALUES(@name, @userName, 1, @admin, NULL);
              SELECT last_insert_rowid();",
            new { name = DisplayNameFor(userName), userName, admin = isAdmin ? 1 : 0 });
    }

    public async Task SetUserActiveAsync(int userId, bool isActive)
    {
        using var c = OpenDb();
        await c.ExecuteAsync(
            "UPDATE Users SET is_active = @a WHERE id = @id;",
            new { a = isActive ? 1 : 0, id = userId });
    }

    /// <summary>Seeds an active team and joins <paramref name="memberUserIds"/> to it. A user who is a
    /// member of NO team has an empty <c>MemberTeamIds</c> and an <c>ActiveTeamId</c> of 0, and every
    /// team-scoped endpoint correctly returns empty for them — so seed the membership.</summary>
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

    /// <summary>Marks a non-working day (HOL-02). Goes through the real repository — the column is
    /// <c>holiday_date</c>, not <c>date</c>, and hand-rolled SQL gets that wrong.</summary>
    public Task SeedHolidayAsync(DateOnly date, string description = "Test holiday") =>
        Services.GetRequiredService<IHolidayRepository>().UpsertAsync(date, description);

    // ---- Clients -----------------------------------------------------------------------------------

    public HttpClient AnonymousClient() => CreateClient();

    /// <summary>A client logged in as <paramref name="userName"/> — THE LOGIN USERNAME, never the display
    /// name (see the seeds above) — with the auth cookie held in the handler's cookie container for every
    /// subsequent request.
    ///
    /// <para><paramref name="connectionId"/> sets <c>X-Connection-Id</c> on every request this client makes.
    /// That header is the ONLY route by which the caller's SignalR connection id reaches the server, and it
    /// is what every mutation must pass as <c>exceptConnectionId</c> so the editing user does not receive
    /// their own echo. Without a way to SET it from a test, that argument is permanently null and the
    /// "except the caller" half of the notify contract cannot be asserted at all.</para></summary>
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

    /// <summary>Idempotently seeds the well-known <see cref="AdminUserName"/> user with
    /// <c>is_admin = 1</c> and logs in as them. The admin belongs to NO team by default — a test that needs
    /// the admin inside a team should seed the user explicitly and pass its id to <see cref="SeedTeamAsync"/>.</summary>
    public async Task<HttpClient> AdminClientAsync(string? connectionId = null)
    {
        using (var c = OpenDb())
        {
            var exists = await c.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM Users WHERE username = @u;", new { u = AdminUserName });
            if (exists == 0)
                await SeedUserAsync(AdminUserName, DefaultPassword, isAdmin: true);
        }

        return await ClientAsync(AdminUserName, DefaultPassword, connectionId);
    }

    /// <summary>Logs in and returns the raw <c>Set-Cookie</c> value, for replaying the cookie against a
    /// DIFFERENT host instance (the Data Protection key-ring restart test).</summary>
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
        if (!disposing || !_ownsRoot) return;

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Temp file occasionally held briefly on Windows; safe to leave for the OS to reap.
        }
    }
}
