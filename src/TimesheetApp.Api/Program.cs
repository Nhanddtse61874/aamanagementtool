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
// The three seams the test host overrides. In production all three fall back to the desktop app's own
// app-local locations, so the API reads the same database the WPF app already uses.
var configPath = builder.Configuration["TimesheetApp:ConfigPath"];
var dbPath = builder.Configuration["TimesheetApp:DbPath"];

IAppConfig appConfig = string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(dbPath)
    ? new JsonAppConfig()
    : new JsonAppConfig(configPath, dbPath);

// The Data Protection key ring MUST outlive the process (see AuthSetup). Default it next to the database
// so it is picked up by whatever already backs the database up.
var keyRingPath = builder.Configuration["TimesheetApp:KeyRingPath"];
if (string.IsNullOrWhiteSpace(keyRingPath))
{
    var dbDir = Path.GetDirectoryName(appConfig.DbPath);
    keyRingPath = Path.Combine(
        string.IsNullOrWhiteSpace(dbDir) ? Directory.GetCurrentDirectory() : dbDir,
        "keys");
}

builder.Services.AddSingleton(appConfig);

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

app.Run();

// Top-level statements emit an INTERNAL Program, so WebApplicationFactory<Program> is CS0122 without this.
public partial class Program;
