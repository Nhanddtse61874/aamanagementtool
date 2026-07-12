using TimesheetApp.Api.Auth;
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

// --- Auth: cookie + Data Protection key ring + the "Admin" policy + FallbackPolicy --------------------
builder.Services.AddApiAuth(keyRingPath);

var app = builder.Build();

// --- One-time startup, in this order ------------------------------------------------------------------
// EnsureBootstrappedAsync is NOT optional: without it a fresh database has ZERO teams, so MemberTeamIds
// is empty for every user and EVERY endpoint returns empty, with nothing to indicate why.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<ITeamBootstrapService>().EnsureBootstrappedAsync();
}

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

api.MapAuthMechanism();   // POST /api/auth/login + /api/auth/logout — the mechanism, not the feature.

app.Run();

// Top-level statements emit an INTERNAL Program, so WebApplicationFactory<Program> is CS0122 without this.
public partial class Program;
