using System.Net;
using System.Net.Http.Json;
using Dapper;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Config;
using TimesheetApp.Data;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>M9: A FRESHLY CLONED REPOSITORY COULD NEVER BE LOGGED INTO. The deadlock, precisely:
///
/// <list type="number">
/// <item>Fresh clone => no database file => <c>DatabaseInitializer</c> creates an EMPTY one.</item>
/// <item>Migration v10 runs <c>UPDATE Users SET is_admin = 1 WHERE id = (SELECT MIN(id) FROM Users)</c>.
///   On zero rows <c>MIN(id)</c> is NULL, so it matches NOTHING and nobody is promoted.</item>
/// <item><c>AdminBootstrap</c> saw <c>all.Count == 0</c>, logged "nothing to bootstrap", and returned.</item>
/// <item>Zero users, zero admins => nobody can log in — and <c>POST /api/users</c> is admin-gated, so the
///   first user could not be created over HTTP either.</item>
/// </list>
///
/// <para>These tests all run against <c>SeedFirstAdmin = true</c> — the PRODUCTION default. The rest of the
/// suite runs with it off; see the note on <see cref="ApiFactory.SeedFirstAdmin"/> for why (short version:
/// <c>AdminBootstrap.DefaultUsername</c> and <c>ApiFactory.AdminUserName</c> are both the literal string
/// "admin", and letting the seed fire under every test broke 38 of them).</para></summary>
public sealed class AdminBootstrapSeedTests
{
    /// <summary>🔴 THE TEST THAT MATTERS. Everything else here is a detail; this is the bug.
    ///
    /// <para>Asserting that a row appeared in <c>Users</c>, or that <c>password_hash</c> went non-null, would
    /// pass even if the seeded credentials did not actually authenticate. So: seed nothing, boot the host on
    /// an empty database exactly as a fresh clone does, and POST the real login route with the documented
    /// default credentials. A 200, a cookie, and an authenticated <c>GET /api/me</c> through that cookie are
    /// the only things that prove a human can get in.</para></summary>
    [Fact]
    public async Task An_empty_database_seeds_an_admin_who_can_actually_log_in()
    {
        using var factory = new ApiFactory { SeedFirstAdmin = true };
        using var client = factory.AnonymousClient();   // boots the host -> empty DB -> the seed runs

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AdminBootstrap.DefaultUsername, AdminBootstrap.DefaultPassword));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // A cookie was actually issued — without it the 200 is a lie the SPA cannot act on.
        Assert.True(login.Headers.Contains("Set-Cookie"), "Login returned 200 but issued NO cookie.");

        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal(AdminBootstrap.DefaultUsername, body!.Username);
        Assert.True(body.IsAdmin, "The seeded first user is not an admin, so every admin route is shut to them.");

        // And the cookie AUTHENTICATES: GET /api/me is behind the FallbackPolicy, so a 200 here means the
        // session the login handed out is real, not just a Set-Cookie header that decodes to nothing.
        var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    /// <summary>🔴 THE ONE THAT WOULD HAVE SHIPPED BROKEN. <c>TeamBootstrapService.EnsureBootstrappedAsync</c>
    /// runs BEFORE <c>AdminBootstrap</c> in Program.cs, so on a fresh database its two "give every user a
    /// team" sweeps —
    /// <c>INSERT OR IGNORE INTO UserTeams … SELECT id FROM Users</c> and
    /// <c>UPDATE Users SET active_team_id … WHERE active_team_id = 0</c> —
    /// both ran while <c>Users</c> was still EMPTY and matched zero rows. The admin seeded moments later was
    /// therefore in NO team, with <c>active_team_id = 0</c>.
    ///
    /// <para>That is not cosmetic, and it is why this test asserts the PRODUCT consequence rather than the
    /// membership row alone: a user in zero teams is REFUSED outright by <c>POST /api/backlogs</c> ("You are
    /// not a member of any team"), sees an empty Backlog list and an empty Task List. They could log in to an
    /// app that did nothing. Program.cs now re-runs the (idempotent) team bootstrap after seeding.</para></summary>
    [Fact]
    public async Task The_seeded_admin_is_a_member_of_a_team()
    {
        using var factory = new ApiFactory { SeedFirstAdmin = true };
        using var client = await factory.ClientAsync(
            AdminBootstrap.DefaultUsername, AdminBootstrap.DefaultPassword);

        // The API's own view of the caller. MemberTeamIds is the authorization bound; ActiveTeamId is what
        // every team-scoped write stamps onto its row. Zero/empty here is the whole bug.
        var me = await (await client.GetAsync("/api/me")).Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotEmpty(me!.MemberTeamIds);
        Assert.True(me.ActiveTeamId > 0, "The seeded admin has NO active team, so every team-scoped route is empty for them.");

        // The database agrees — a membership row genuinely exists.
        using (var db = factory.OpenDb())
        {
            var memberships = await db.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM UserTeams ut
                  JOIN Users u ON u.id = ut.user_id
                  WHERE u.username = @u;",
                new { u = AdminBootstrap.DefaultUsername });
            Assert.Equal(1L, memberships);
        }

        // THE PROOF THAT THE APP IS USABLE: this route 400s a teamless caller by design. A 200 means the
        // account we handed the operator can actually create work, not just authenticate.
        var created = await client.PostAsJsonAsync("/api/backlogs", MinimalCreate("SEED-1"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    /// <summary>The seed exists for an EMPTY database and nothing else. A database that already has people in
    /// it must never grow a surprise admin account with a default password — so the guard is
    /// <c>all.Count == 0</c>, not <c>admins.Count == 0</c>.
    ///
    /// <para>The sharp edge, and the one this test picks: a database with users but NO admin (v10's
    /// <c>MIN(id)</c> promotion is supposed to make that impossible). Bootstrap must WARN, not seed.</para></summary>
    [Fact]
    public async Task A_database_that_already_has_users_is_NEVER_seeded()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            await SeedExistingUserAsync(root, "alice");   // exists BEFORE the host boots

            using var factory = new ApiFactory(root) { SeedFirstAdmin = true };
            _ = factory.Services;   // boot -> AdminBootstrap runs

            using var db = factory.OpenDb();

            var total = await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users;");
            Assert.Equal(1L, total);   // alice, and only alice

            var seeded = await db.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM Users WHERE username = @u;",
                new { u = AdminBootstrap.DefaultUsername });
            Assert.Equal(0L, seeded);

            Assert.DoesNotContain(factory.Logs, l => l.Contains("FIRST ADMIN CREATED"));
            Assert.Contains(factory.Logs, l => l.Contains("NONE has is_admin"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>A real deployment must not be stuck with admin/admin. Both halves matter: the configured
    /// account works, AND the default account does not exist to be guessed.</summary>
    [Fact]
    public async Task The_seeded_credentials_can_be_overridden_by_configuration()
    {
        const string username = "boss";
        const string password = "S3cret-Passphrase!";

        using var factory = new ApiFactory
        {
            SeedFirstAdmin = true,
            BootstrapAdminUsername = username,
            BootstrapAdminPassword = password,
        };

        using var client = factory.AnonymousClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(body!.IsAdmin);

        // The default credentials must NOT also be lying around.
        using var anonymous = factory.AnonymousClient();
        var denied = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AdminBootstrap.DefaultUsername, AdminBootstrap.DefaultPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    /// <summary>IDEMPOTENCE. The API restarts; the seed must not fire twice. Two admins named "admin" is not
    /// a cosmetic duplicate — <c>GetCredentialsAsync</c> reads with <c>QuerySingleOrDefaultAsync</c>, so a
    /// second row makes it THROW and locks everybody out of the account the seed exists to provide.
    ///
    /// <para>It must also not re-claim the password: an operator who has changed it would be silently reset
    /// on the next reboot.</para></summary>
    [Fact]
    public async Task Bootstrap_run_twice_creates_exactly_one_admin()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            string hashAfterFirstBoot;
            using (var first = new ApiFactory(root) { SeedFirstAdmin = true })
            {
                _ = first.Services;
                Assert.Contains(first.Logs, l => l.Contains("FIRST ADMIN CREATED"));

                hashAfterFirstBoot = await ReadHashAsync(first, AdminBootstrap.DefaultUsername);
                Assert.False(string.IsNullOrEmpty(hashAfterFirstBoot));
            }

            // The restart — same database, same seed setting.
            using var second = new ApiFactory(root) { SeedFirstAdmin = true };
            _ = second.Services;

            Assert.DoesNotContain(second.Logs, l => l.Contains("FIRST ADMIN CREATED"));

            using (var db = second.OpenDb())
            {
                var admins = await db.ExecuteScalarAsync<long>(
                    "SELECT COUNT(*) FROM Users WHERE username = @u;",
                    new { u = AdminBootstrap.DefaultUsername });
                Assert.Equal(1L, admins);
            }

            // The password survived untouched, and STILL LOGS IN.
            Assert.Equal(hashAfterFirstBoot, await ReadHashAsync(second, AdminBootstrap.DefaultUsername));

            using var client = second.AnonymousClient();
            var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest(AdminBootstrap.DefaultUsername, AdminBootstrap.DefaultPassword));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>Puts a user into the database BEFORE the API host starts — the only way to present bootstrap
    /// with a non-empty database, since it runs during startup. Mirrors
    /// <c>AdminBootstrapTests.SeedAdminWithNoPasswordAsync</c>.</summary>
    private static async Task SeedExistingUserAsync(string root, string username)
    {
        Directory.CreateDirectory(root);

        var config = new JsonAppConfig(
            Path.Combine(root, "appsettings.json"), Path.Combine(root, "timesheet.db"));
        var connections = new SqliteConnectionFactory(config, SqliteProfile.Server);
        await new DatabaseInitializer(connections).InitializeAsync();

        using var db = connections.Create();
        await db.ExecuteAsync(
            @"INSERT INTO Users(name, username, is_active, is_admin, password_hash)
              VALUES('Alice Nguyen', @username, 1, 0, 'x');",
            new { username });
    }

    private static async Task<string> ReadHashAsync(ApiFactory factory, string username)
    {
        using var db = factory.OpenDb();
        return await db.ExecuteScalarAsync<string>(
            "SELECT password_hash FROM Users WHERE username = @u;", new { u = username }) ?? "";
    }

    private static BacklogCreateRequest MinimalCreate(string code) =>
        new(code, "ARCS", null, null, null, null, null, null, null, null, null, null, null, null);

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
