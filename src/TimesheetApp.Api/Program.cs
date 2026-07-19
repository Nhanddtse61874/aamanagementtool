using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Endpoints;
using TimesheetApp.Api.Hubs;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;

var builder = WebApplication.CreateBuilder(args);

// =====================================================================================================
// SCOPE VALIDATION, ALWAYS ON — not just in Development.
//
// This single setting is the milestone's cheapest insurance. The WPF composition root registers 43
// services as AddSingleton, which is CORRECT on a desktop (one process serves one user, so per-user
// singleton state is per-user by construction) and a CROSS-USER DATA LEAK on a server. With validation
// on, a captive dependency ("Cannot consume scoped service 'ICurrentTeamService' from singleton
// 'ITimeLogService'") fails ONE named test at startup instead of quietly serving user A's active team to
// user B. ValidateOnBuild makes it fail at Build(), not on the first request that happens to touch it.
// =====================================================================================================
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateScopes = true;
    o.ValidateOnBuild = true;
});

// --- Config + paths -----------------------------------------------------------------------------------
// M11: ConfigPath / DbPath / KeyRingPath are REQUIRED IConfiguration keys, with NO fallback chain. Before
// M11, an unset DbPath (even with ConfigPath set -- the `||` at F1) fell straight through to
// JsonAppConfig()'s desktop-app %APPDATA% defaults: the operator got a running app pointed at the WRONG
// database with no signal at all (fast-lane-settings-appsettings.json, F1). The fallback chain WAS the
// bug; RequireConfig below refuses to start instead of guessing.
//
// appsettings.json now WINS over the persisted store for DbPath too (F2's fix, decided in
// docs/superpowers/specs/2026-07-19-m11-configuration-design.md — the go-live requirement is
// "DbPath from appsettings.json; existing file opened, absent file created", which is meaningless if a
// stale %APPDATA% file could still outrank it). The value resolved below is AUTHORITATIVE -- see
// JsonAppConfig's ctor. On a machine that has run this app before, editing appsettings.json now changes
// which database it opens. That is the intent, and it is also a foot-gun: the banner further down prints
// the path that ACTUALLY won, every start.
static string RequireConfig(IConfiguration config, string key, string legacyHint)
{
    var value = config[$"TimesheetApp:{key}"];
    if (!string.IsNullOrWhiteSpace(value)) return value;

    Console.WriteLine("======================================================================");
    Console.WriteLine($"  FATAL: required configuration key 'TimesheetApp:{key}' is not set.");
    Console.WriteLine( "  Refusing to start rather than guess -- an unset location key used to fall");
    Console.WriteLine( "  through to the wrong database with no warning at all.");
    Console.WriteLine();
    Console.WriteLine( "  Set it in appsettings.json, e.g.:");
    Console.WriteLine($"    {{ \"TimesheetApp\": {{ \"{key}\": \"...\" }} }}");
    Console.WriteLine($"  or as the environment variable TimesheetApp__{key}.");
    Console.WriteLine();
    Console.WriteLine($"  Legacy (pre-M11) value on this machine: {legacyHint}");
    Console.WriteLine("======================================================================");
    throw new InvalidOperationException(
        $"Missing required configuration: TimesheetApp:{key}. See the console output above.");
}

var legacyAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

var configPath = RequireConfig(builder.Configuration, "ConfigPath",
    Path.Combine(legacyAppData, "TimesheetApp", "appsettings.json") +
    $" (an existing file there is migrated automatically to {JsonAppConfig.DefaultConfigPath()} -- F5 -- " +
    "if you point ConfigPath at the new name instead)");
var dbPath = RequireConfig(builder.Configuration, "DbPath", JsonAppConfig.DefaultDbPath());
var keyRingPath = RequireConfig(builder.Configuration, "KeyRingPath",
    Path.Combine(Path.GetDirectoryName(JsonAppConfig.DefaultDbPath())!, "keys"));

// ArchivePath: OPTIONAL, unlike the three above -- it has a working default (a folder next to DbPath; see
// StandupArchiveService / TaskListArchiveService) and breaking that default is not in this milestone's
// scope. Only wired to IConfiguration so an operator CAN override it the same way, without being forced
// to set it.
var archivePath = builder.Configuration["TimesheetApp:ArchivePath"];

IAppConfig appConfig = new JsonAppConfig(configPath, dbPath, archivePath);
builder.Services.AddSingleton(appConfig);

// --- Startup diagnostics: log WHICH database and config the process actually opened -------------------
// A second machine that "cannot log in" almost always means the API opened a DIFFERENT database than
// expected. This is the one place that reveals the resolved path BEFORE anything destructive (RestoreCli)
// or confusing (a wrong login) can happen -- make it loud. Console.WriteLine, not ILogger: the logging
// pipeline is not built yet at this point (app = builder.Build() is below), and this must print regardless
// of how logging is later configured. appConfig.DbPath here is the path that ACTUALLY won (F2: it always
// equals `dbPath` above, never a stale value from the persisted store).
Console.WriteLine("======================================================================");
Console.WriteLine($"  Database : {appConfig.DbPath}");
Console.WriteLine($"  Config   : {configPath}");
Console.WriteLine($"  KeyRing  : {keyRingPath}");
Console.WriteLine("======================================================================");

// --- Offline restore CLI branch (M10 Blocker 2 / P2) ----------------------------------------------------
// See RestoreCli's doc comment for the full "why" and the six amendments this shape had to satisfy. Short
// version: WPF's Settings tab was the only production caller of IBackupService.RestoreAsync, and M10
// deletes it -- this replaces it with an OFFLINE CLI run against a STOPPED API, never an in-app admin
// route (restoring while the app runs is the exact hazard this design avoids).
//
// MUST sit AFTER the banner above, NEVER before (HOLE 11): JsonAppConfig resolves per-Windows-user paths,
// so running this blind can restore into the WRONG database and report success -- the banner is the one
// diagnostic that reveals a wrong resolution before anything destructive happens. Returns here, BEFORE
// builder.Build() -- no Kestrel, no DI container is ever created for a restore; RestoreAsync only needs
// the IAppConfig already resolved above.
if (RestoreCli.IsRestoreCommand(args))
    return await RestoreCli.RunAsync(args, appConfig, Console.Out);

// =====================================================================================================
// SqliteProfile.Server — EXPLICIT, because the constructor's default is Desktop.
//
//     public SqliteConnectionFactory(IAppConfig config, SqliteProfile profile = SqliteProfile.Desktop)
//
// A plain AddSingleton<IConnectionFactory, SqliteConnectionFactory>() takes that default SILENTLY, and
// then: (1) journal_mode=DELETE, so readers block writers and the API serialises every request; and
// (2) no DefaultTimeout, so Microsoft.Data.Sqlite auto-retries SQLITE_BUSY up to CommandTimeout — a
// measured 33,940 ms before a blocked writer failed, not the 5,000 the Server profile bounds it to.
//
// The factory itself is stateless (Create() hands back a fresh connection the caller owns and disposes),
// so Singleton is right. HostBootTests asserts PRAGMA journal_mode = wal from THIS container.
// =====================================================================================================
builder.Services.AddSingleton<IConnectionFactory>(sp =>
    new SqliteConnectionFactory(sp.GetRequiredService<IAppConfig>(), SqliteProfile.Server));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IJournalWarningSink, TraceJournalWarningSink>();
builder.Services.AddHttpContextAccessor();

// --- Repositories: genuinely stateless (one short connection per method) -> Singleton -----------------
builder.Services.AddSingleton<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IBacklogRepository, BacklogRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<ITimeLogRepository, TimeLogRepository>();
builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();
builder.Services.AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>();
builder.Services.AddSingleton<IDefaultTaskRepository, DefaultTaskRepository>();
builder.Services.AddSingleton<IStandupRepository, StandupRepository>();
builder.Services.AddSingleton<ITagRepository, TagRepository>();
builder.Services.AddSingleton<IPcaContactRepository, PcaContactRepository>();
builder.Services.AddSingleton<IHolidayRepository, HolidayRepository>();
builder.Services.AddSingleton<ITeamRepository, TeamRepository>();

// --- Stateless / config-only services -> Singleton ----------------------------------------------------
builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
builder.Services.AddSingleton<IWorkingDayCalculator, WorkingDayCalculator>();
builder.Services.AddSingleton<IScheduleStateService, ScheduleStateService>();
builder.Services.AddSingleton<ISmartInputService, SmartInputService>();
builder.Services.AddSingleton<IReportAggregator, ReportAggregator>();
builder.Services.AddSingleton<IPathSanitizer, PathSanitizer>();
builder.Services.AddSingleton<IDbBackupHelper, DbBackupHelper>();
builder.Services.AddSingleton<IBackupService, BackupService>();
builder.Services.AddSingleton<IExportService, ExportService>();
builder.Services.AddSingleton<ISharePointDestinationValidator, SharePointDestinationValidator>();
builder.Services.AddSingleton<IStandupArchiveService, StandupArchiveService>();
builder.Services.AddSingleton<ITaskListArchiveService, TaskListArchiveService>();
// M9 (P1c): the Task List read model. Singleton is safe BECAUSE teamIds is a method parameter — it
// injects no per-user state, so it is not a captive dependency under ValidateScopes. P3 maps the endpoint.
builder.Services.AddSingleton<ITaskListService, TaskListService>();
builder.Services.AddSingleton<IExportHubService, ExportHubService>();
builder.Services.AddSingleton<IPruneArchiver, PruneArchiver>();
builder.Services.AddSingleton<IRetentionService, RetentionService>();
builder.Services.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
builder.Services.AddSingleton<ITeamBootstrapService, TeamBootstrapService>();

// B3 (M10 blocker 3): the four best-effort startup jobs App.xaml.cs used to run (auto-backup, export-hub
// backfill, standup/task-list archive backfill) now that the WPF app is the thing being retired. See
// StartupJobsHostedService's own doc comment for why this is once-per-process-start, not a recurring
// timer, and why registration stays unconditional (including under the "Testing" environment) while
// EXECUTION is gated inside the service itself.
builder.Services.AddHostedService<StartupJobsHostedService>();

// =====================================================================================================
// SCOPED — everything that holds PER-USER state, and everything that transitively injects one.
//
//   ICurrentUserService  holds Current (the resolved User)
//   ICurrentTeamService  holds ActiveTeamId
//   IClientContext       holds the identity + the authorization bound + the connection id
//
//   ITimeLogService              -> ICurrentTeamService
//   IStandupService              -> ICurrentUserService + ICurrentTeamService
//   IBacklogContinuationService  -> ICurrentUserService
//
// Register any of these as Singleton and one user's identity serves every other user's request.
// =====================================================================================================
builder.Services.AddScoped<ICurrentUserService>(sp =>
    // CurrentUserService is NOT modified: it already takes its identity through a Func<string> seam.
    // WPF passes () => Environment.UserName; the API passes the cookie's name claim. Empty string when
    // unauthenticated -> GetByUsernameAsync("") -> null -> NeedsSelection -> ClientContextFilter 401s.
    new CurrentUserService(
        sp.GetRequiredService<IUserRepository>(),
        () => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User.Identity?.Name ?? ""));

builder.Services.AddScoped<ICurrentTeamService, ApiCurrentTeamService>();
builder.Services.AddScoped<ApiClientContext>();
builder.Services.AddScoped<IClientContext>(sp => sp.GetRequiredService<ApiClientContext>());
builder.Services.AddScoped<ClientContextFilter>();

builder.Services.AddScoped<ITimeLogService, TimeLogService>();
builder.Services.AddScoped<IStandupService, StandupService>();
builder.Services.AddScoped<IBacklogContinuationService, BacklogContinuationService>();

// =====================================================================================================
// Wave 3 — the real cross-user push. DataHub resolves the caller and joins per-team SignalR groups
// itself (hubs do not run endpoint filters, so IClientContext is unavailable there — see DataHub.cs).
// SignalRChangeNotifier sends to those same groups, or AllExcept for the teamId: 0 global sentinel,
// replacing the in-process, single-tab-only WeakReferenceMessenger the WPF app uses. Touches NO
// endpoint file — the entire point of shipping IChangeNotifier as a seam back in Wave 1.
// =====================================================================================================
builder.Services.AddSignalR();
builder.Services.AddSingleton<IChangeNotifier, SignalRChangeNotifier>();

// --- Auth: cookie + Data Protection key ring + the "Admin" policy + FallbackPolicy --------------------
builder.Services.AddApiAuth(keyRingPath);

// --- OpenAPI: M8.4 GENERATES ITS TYPESCRIPT CLIENT FROM THIS DOCUMENT ---------------------------------
// It is not documentation. The vendored Angular bundle's models are unusable as a wire contract (User has
// no id, Backlog has no id, and HoursMap is keyed by `${groupIndex}-${taskIndex}-${dayIndex}` — ARRAY
// INDICES, so one sort or filter puts hours on the wrong task). Without the document, M8.4 has nothing to
// generate from and someone hand-types those types back into existence.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- One-time startup, in this order ------------------------------------------------------------------
// EnsureBootstrappedAsync is NOT optional: without it a fresh database has ZERO teams, so MemberTeamIds
// is empty for every user and EVERY endpoint returns empty, with nothing to indicate why.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

    var teamBootstrap = scope.ServiceProvider.GetRequiredService<ITeamBootstrapService>();
    await teamBootstrap.EnsureBootstrappedAsync();

    var seededFirstAdmin = await AdminBootstrap.EnsureAdminPasswordAsync(
        scope.ServiceProvider.GetRequiredService<IUserRepository>(),
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AdminBootstrap"),
        scope.ServiceProvider.GetRequiredService<IConfiguration>());

    if (seededFirstAdmin)
    {
        // ORDERING HAZARD, AND THE REASON THIS SECOND CALL EXISTS.
        //
        // EnsureBootstrappedAsync above ran while Users was still EMPTY, so the two sweeps inside its
        // backfill that exist to give users a team --
        //     INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;
        //     UPDATE Users SET active_team_id = @t WHERE active_team_id = 0;
        // -- both matched ZERO ROWS. The admin AdminBootstrap has just seeded therefore belongs to NO
        // team and has active_team_id = 0 (the column default).
        //
        // That is not cosmetic. A user in zero teams can log in and then do nothing: ActiveTeamId
        // resolves to 0, so POST /api/backlogs REFUSES them outright ("You are not a member of any
        // team"), GET /api/backlogs matches nothing, and the Task List is empty. They would have been
        // handed the keys to an empty room.
        //
        // Re-running it now that the admin exists costs one no-op pass over already-migrated rows --
        // the backfill is idempotent BY DESIGN (INSERT OR IGNORE, WHERE team_id IS NULL,
        // WHERE active_team_id = 0), which is the same property that lets it self-heal an interrupted
        // migration on every startup. It takes the `existing is not null` branch, so it does NOT create
        // a second team and does NOT take a backup.
        await teamBootstrap.EnsureBootstrappedAsync();
    }
}

// OUTERMOST: it must wrap every endpoint, including anything a later wave adds.
// ConcurrencyConflictException -> 409 + ConflictBody. ArgumentException -> 400 + ValidationBody.
// The business-rule channel (SaveResult.Ok == false) NEVER throws and so never reaches this — the ENDPOINT
// must check Ok and return 400 itself.
app.UseMiddleware<ExceptionMapper>();

// Swagger sits BEFORE the authorization middleware on purpose: FallbackPolicy = DefaultPolicy applies to
// "requests served by other middleware after the authorization middleware", so a document served after it
// would 401 — and M8.4's code generator (and the Wave-2 route audit) could not read it without a cookie.
app.UseSwagger();
app.UseSwaggerUI();

// Serve the built Angular app (if present) from wwwroot. A no-op in dev (no wwwroot) — `ng serve` still
// proxies to the API as before. In a deployed build, deploy-local.bat copies the Angular output into
// wwwroot so the API serves the UI and /api on ONE origin (no proxy, so the SameSite=Lax cookie is
// same-origin and survives). These sit BEFORE UseAuthentication ON PURPOSE: the app shell, its JS/CSS and
// the login page must load without a cookie. As MIDDLEWARE placed ahead of the authorization middleware,
// static files are not subject to the FallbackPolicy, so they serve anonymously without AllowAnonymous.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Health is anonymous — the FallbackPolicy would otherwise 401 it, and a health check that requires a
// login is not a health check.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .AllowAnonymous()
   .WithTags("Health")
   .ExcludeFromDescription();

// =====================================================================================================
// THE API GROUP. Every endpoint below runs ClientContextFilter, so IClientContext is populated for all
// of them and no endpoint author has to remember to do it.
//
// The prefix is EMPTY ON PURPOSE: endpoint files write their FULL route ("/api/timesheet/week"), exactly
// as the route table reads. A "/api" group prefix would silently turn the natural, correct-looking
// "/api/timesheet/week" into "/api/api/timesheet/week".
// =====================================================================================================
var api = app.MapGroup("").AddEndpointFilter<ClientContextFilter>();

// The auth MECHANISM: POST /api/auth/login, POST /api/auth/logout, GET /api/me. Not Wave 2's to re-map.
api.MapAuthMechanism();

// All four registered NOW, against stubs. Wave 2 fills in exactly one file each and NEVER TOUCHES THIS
// FILE — which is precisely what makes four parallel agents safe to run.
api.MapAuthEndpoints();       // W2-A
api.MapTimesheetEndpoints();  // W2-B
api.MapBacklogEndpoints();    // W2-C
api.MapSettingsEndpoints();   // W2-D

// M9 (P3). `api`, NEVER `app` — same reason as every line above it. `app.MapTaskListEndpoints()` would
// compile and route correctly, but it would BYPASS ClientContextFilter, so IClientContext would never be
// populated: ctx.MemberTeamIds would be empty on every request, the Task List would render blank for
// everyone, and ctx.IsAdmin would be false, silently 403-ing every admin route in AdminEndpoints.
api.MapTaskListEndpoints();   // M9 P3b/P3f — the Task List screen + its monthly markdown
api.MapAdminEndpoints();      // M9 P3c/P3d/P3e — admin flag, settings store, standup archive

// OUTSIDE the `api` group on purpose: SignalR hubs do not run endpoint filters, so
// ClientContextFilter would never fire for it anyway. DataHub authenticates and resolves the
// caller's teams itself (see DataHub.cs). [Authorize] on the hub requires the connection be
// authenticated the same way every other route under FallbackPolicy does.
app.MapHub<DataHub>("/hubs/data");

// SPA fallback: any non-API, non-file path serves index.html so Angular's client-side router handles it.
// MapFallbackToFile is the LOWEST-priority endpoint, so it never shadows a mapped route — /api/*, /hubs/*,
// /health and swagger all match first (asserted in SpaFallbackTests). AllowAnonymous is REQUIRED, not
// cosmetic: FallbackPolicy = DefaultPolicy = RequireAuthenticatedUser, so without it a logged-out user
// requesting a client route (or index.html itself) would get 401 and could never reach the login page —
// AuthSetup.cs says exactly this ("the SPA fallback and static files MUST be explicitly [AllowAnonymous]").
// A no-op when wwwroot is absent (dev / test): the file is not found and the request 404s, /api/* untouched.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
return 0;

// Top-level statements emit an INTERNAL Program, so WebApplicationFactory<Program> is CS0122 without this.
public partial class Program;
