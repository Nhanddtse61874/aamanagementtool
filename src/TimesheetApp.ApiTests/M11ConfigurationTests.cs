using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TimesheetApp.Api.Contracts;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>M11 (docs/superpowers/specs/2026-07-19-m11-configuration-design.md): DbPath / ConfigPath /
/// KeyRingPath become required <c>IConfiguration</c> keys with NO fallback chain (F1), and appsettings.json
/// now WINS over the persisted <c>JsonAppConfig</c> store for DbPath (F2). This file is the verification
/// list from the design spec's §9, one test per bullet.
///
/// <para>🔴 SAFETY: every test here pins ConfigPath/DbPath/KeyRingPath into a FRESH temp directory that did
/// not exist before the test ran (<see cref="ApiFactory.NewRoot"/>) — never the real
/// <c>%APPDATA%\TimesheetApp\...</c> or <c>C:\Users\Admin\Documents\TimesheetApp\timesheet.db</c>. The one
/// test that touches a REAL repo file (<see cref="UseSetting_still_beats_a_real_appsettings_json_in_the_api_project"/>)
/// touches ONLY <c>src/TimesheetApp.Api/appsettings.json</c> — never the WPF/API's %APPDATA% store — and
/// restores it in a <c>finally</c> block, mirroring the F3 experiment's own cleanup discipline.</para></summary>
public sealed class M11ConfigurationTests
{
    // ---- DbPath is honoured, and precedence is right ----------------------------------------------------

    /// <summary>Spec §9, bullet 1: "Fresh path in appsettings.json, no file there → a new database is
    /// created at exactly that path." <see cref="ApiFactory"/>'s <c>DbPath</c> is <c>Root/timesheet.db</c>,
    /// a path that cannot exist before the factory's own fresh <see cref="ApiFactory.NewRoot"/> directory
    /// does — so this is the honest "does the resolved path get a DB created there" proof, not a tautology.</summary>
    [Fact]
    public void Fresh_DbPath_from_config_creates_a_new_database_at_exactly_that_path()
    {
        using var factory = new ApiFactory();
        Assert.False(File.Exists(factory.DbPath));

        _ = factory.Services; // forces the host (and DatabaseInitializer.InitializeAsync) to run.

        Assert.True(File.Exists(factory.DbPath));
    }

    /// <summary>Spec §9, bullet 2: "Existing database at that path → it is opened, not replaced. Assert row
    /// counts survive." Mirrors the established "two factories, one root = a restart" pattern
    /// (<c>AuthTests.The_data_protection_key_ring_survives_a_restart</c>): a second host over the SAME root
    /// must see the first host's data, proving <c>DatabaseInitializer</c> opened the existing file rather
    /// than creating a fresh empty one.</summary>
    [Fact]
    public async Task Existing_database_at_the_configured_path_is_opened_not_replaced()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            int userId;
            using (var first = new ApiFactory(root))
            {
                userId = await first.SeedUserAsync("frank", ApiFactory.DefaultPassword);
                await first.SeedTeamAsync("Team F", userId);
            }

            // A brand-new host, same root -- the "restart".
            using var second = new ApiFactory(root);
            using var client = await second.ClientAsync("frank");
            var me = await client.GetFromJsonAsync<MeResponse>("/api/me");

            Assert.NotNull(me);
            Assert.Equal(userId, me!.Id);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Spec §9, bullet 4: "A stale %APPDATA% store carrying a different DbPath → appsettings.json
    /// still wins." F2's exact trap, exercised through the REAL <c>Program.cs</c> wiring (not just the
    /// <c>JsonAppConfig</c> unit tests in <c>TimesheetApp.Tests</c>): a persisted store already sitting at
    /// ConfigPath, naming a DIFFERENT database, must never be opened.</summary>
    [Fact]
    public void A_stale_persisted_store_carrying_a_different_DbPath_does_not_win()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var configPath = Path.Combine(root, "appsettings.json");
            var staleDbPath = Path.Combine(root, "stale-timesheet.db");
            var realDbPath = Path.Combine(root, "timesheet.db"); // == ApiFactory(root).DbPath

            // A persisted store that ALREADY names a different DbPath -- exactly F2's trap: "any machine
            // that has run the app before" has one of these sitting at ConfigPath.
            File.WriteAllText(configPath, "{\"DbPath\":\"" + staleDbPath.Replace(@"\", @"\\") + "\"}");

            using var factory = new ApiFactory(root); // UseSetting pins DbPath to realDbPath
            _ = factory.Services;

            Assert.True(File.Exists(realDbPath));    // appsettings (UseSetting, standing in for it) won
            Assert.False(File.Exists(staleDbPath));  // the persisted store's DbPath was never touched
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- Missing required config refuses to start (F1) ---------------------------------------------------

    /// <summary>Spec §9, bullet 3: "A location key missing → the process refuses to start and names the
    /// missing key." Three tests, one per required key — <see cref="MissingKeyFactory"/> omits exactly one
    /// <c>UseSetting</c> so the other two stay pinned into a fresh temp directory (never a real path).</summary>
    [Fact]
    public void Missing_ConfigPath_refuses_to_start_and_names_the_key()
    {
        var root = ApiFactory.NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new MissingKeyFactory(
                configPath: null,
                dbPath: Path.Combine(root, "timesheet.db"),
                keyRingPath: Path.Combine(root, "keys"));

            var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
            Assert.Contains("TimesheetApp:ConfigPath", ex.ToString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Missing_DbPath_refuses_to_start_and_names_the_key()
    {
        var root = ApiFactory.NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new MissingKeyFactory(
                configPath: Path.Combine(root, "appsettings.json"),
                dbPath: null,
                keyRingPath: Path.Combine(root, "keys"));

            var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
            Assert.Contains("TimesheetApp:DbPath", ex.ToString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Missing_KeyRingPath_refuses_to_start_and_names_the_key()
    {
        var root = ApiFactory.NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new MissingKeyFactory(
                configPath: Path.Combine(root, "appsettings.json"),
                dbPath: Path.Combine(root, "timesheet.db"),
                keyRingPath: null);

            var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
            Assert.Contains("TimesheetApp:KeyRingPath", ex.ToString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>The converse of the three tests above: ArchivePath is deliberately NOT required (spec §9
    /// scope, "Any change to how DatabaseInitializer creates a missing database" is out of scope, and
    /// ArchivePath "has a working default"). Omitting it must NOT refuse to start — every other test in
    /// this file already omits it implicitly (via <see cref="ApiFactory"/>, which never sets it), so this
    /// makes that coverage explicit rather than merely incidental.</summary>
    [Fact]
    public void Missing_ArchivePath_does_not_refuse_to_start()
    {
        using var factory = new ApiFactory(); // never sets TimesheetApp:ArchivePath
        var provider = factory.Services;

        Assert.NotNull(provider);
    }

    // ---- F3 regression guard: UseSetting must still beat a real appsettings.json ------------------------

    /// <summary>Spec §9, bullet 5 / F3 (fast-lane-settings-appsettings.json): proven empirically 2026-07-19
    /// (three runs, a positive control) that <c>WebApplicationFactory.UseSetting</c> beats a REAL
    /// <c>appsettings.json</c> in <c>TimesheetApp.Api</c>'s content root. That was a manual, one-time proof;
    /// this automates it so a future change to config wiring cannot silently flip precedence and retarget
    /// every one of the ~530 API tests at whatever DbPath a committed <c>appsettings.json</c> happens to
    /// name.
    ///
    /// <para>🔴 SAFETY: the sentinel path is a fresh, nonexistent temp directory — never anything resembling
    /// the real company database. The file this test writes to is
    /// <c>src/TimesheetApp.Api/appsettings.json</c> (real ASP.NET Core config, per F5) — NOT
    /// <c>%APPDATA%\TimesheetApp\...</c> (the JsonAppConfig writable store) and NOT the real database. The
    /// original file/absence is restored in a <c>finally</c> block even if the assertion fails, mirroring
    /// the F3 experiment's own cleanup discipline ("the experimental appsettings.json was DELETED").</para></summary>
    [Fact]
    public async Task UseSetting_still_beats_a_real_appsettings_json_in_the_api_project()
    {
        var appSettingsPath = ApiAppSettingsPath();
        var sentinelRoot = Path.Combine(Path.GetTempPath(), "tsapi-f3-sentinel-" + Guid.NewGuid().ToString("N"));
        var sentinelDbPath = Path.Combine(sentinelRoot, "sentinel.db");
        Assert.False(Directory.Exists(sentinelRoot)); // never existed before this test

        var originalExisted = File.Exists(appSettingsPath);
        var originalContent = originalExisted ? await File.ReadAllTextAsync(appSettingsPath) : null;

        await File.WriteAllTextAsync(appSettingsPath,
            "{\"TimesheetApp\":{\"DbPath\":\"" + sentinelDbPath.Replace(@"\", @"\\") + "\"}}");
        try
        {
            // ApiFactory.ConfigureWebHost pins DbPath to ITS OWN fresh root via UseSetting -- see ApiFactory.cs.
            using var factory = new ApiFactory();
            using var client = factory.AnonymousClient();

            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // The sentinel path named in appsettings.json must NEVER have been touched...
            Assert.False(Directory.Exists(sentinelRoot));
            Assert.False(File.Exists(sentinelDbPath));

            // ...and the host actually opened ApiFactory's OWN db -- proving UseSetting's value won, not
            // merely that appsettings.json went unread.
            Assert.True(File.Exists(factory.DbPath));
        }
        finally
        {
            if (originalExisted) await File.WriteAllTextAsync(appSettingsPath, originalContent!);
            else File.Delete(appSettingsPath);
        }
    }

    /// <summary>Walks up from the test assembly's output directory to find <c>TimesheetApp.sln</c>, then
    /// resolves <c>TimesheetApp.Api/appsettings.json</c> from there -- the same content root
    /// <c>WebApplicationFactory&lt;Program&gt;</c> resolves internally (proven by F3's positive control: a
    /// deliberately malformed file at this exact path broke every host construction).</summary>
    private static string ApiAppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimesheetApp.sln")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                "Could not locate TimesheetApp.sln walking up from " + AppContext.BaseDirectory);

        return Path.Combine(dir.FullName, "TimesheetApp.Api", "appsettings.json");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>A minimal <c>WebApplicationFactory&lt;Program&gt;</c> that omits exactly one of the three
    /// required <c>TimesheetApp:*</c> settings (pass <c>null</c> for it), so the fail-fast path in
    /// <c>Program.cs</c> fires for that key alone. <see cref="ApiFactory"/> is sealed and always sets all
    /// three, so it cannot express this on its own — mirrors why <c>SignalRTestFactory</c> exists as a
    /// second, minimal fixture rather than subclassing it.</summary>
    private sealed class MissingKeyFactory : WebApplicationFactory<Program>
    {
        private readonly string? _configPath;
        private readonly string? _dbPath;
        private readonly string? _keyRingPath;

        public MissingKeyFactory(string? configPath, string? dbPath, string? keyRingPath)
        {
            _configPath = configPath;
            _dbPath = dbPath;
            _keyRingPath = keyRingPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (_configPath is not null) builder.UseSetting("TimesheetApp:ConfigPath", _configPath);
            if (_dbPath is not null) builder.UseSetting("TimesheetApp:DbPath", _dbPath);
            if (_keyRingPath is not null) builder.UseSetting("TimesheetApp:KeyRingPath", _keyRingPath);
            builder.UseSetting("TimesheetApp:SeedFirstAdmin", "false");
            builder.UseEnvironment("Testing");
        }
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
