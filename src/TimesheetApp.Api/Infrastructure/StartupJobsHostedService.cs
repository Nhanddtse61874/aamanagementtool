using TimesheetApp.Config;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>B3 (M10 blocker 3): ports the four best-effort startup jobs that used to run ONLY from
/// <c>TimesheetApp/App.xaml.cs</c>'s <c>OnStartup</c> — the WPF app being retired in M10 — so a web-only
/// deployment still gets them. Mirrors <c>App.xaml.cs:63-106</c> byte-for-byte: same four calls, same
/// order, same either/or branching on whether an export root is configured, same "never block, never take
/// the process down" contract. What changes is WHERE the calls come from (a hosted service instead of a
/// WPF composition root) and HOW failures are surfaced (<see cref="ILogger"/> instead of
/// <c>Trace.TraceWarning</c>, which has no listener in a server process).
///
/// <para><b>Deliberately NOT a recurring <c>PeriodicTimer</c>.</b> The desktop app ran these once per
/// launch, never more often. Today's deployment (<c>deploy-local.bat</c> / <c>start-web.bat</c>) is a
/// console window started and stopped by one operator on one machine — the SAME process lifetime the WPF
/// app had, not a long-lived always-on server (<c>.planning/STATE.md</c> / <c>ROADMAP.md</c> record
/// remote/unattended hosting as a separate, unresolved blocker). A recurring timer would be a BEHAVIOR
/// INCREASE over the desktop, and — worse — job 2's whole-DB copy
/// (<c>ExportHubService.cs:145</c>, <c>BackupToFolderAsync(Path.Combine(root, "db"), ...)</c>) has NO
/// once-per-period guard of its own (only the per-file markdown writes do), so a timer would chew through
/// its 30-deep prune window in hours. Once per process start reproduces exactly the exposure the desktop
/// app already had — no more, no less.</para>
///
/// <para><b>Operational precondition this does NOT fix and must not pretend to fix:</b> these jobs only
/// run while this process is actually running. An unattended <c>dotnet run</c> console window that nobody
/// opens (or that is running attended and then abandoned for days, as <c>start-web.bat</c>'s "do not
/// close" window title actively invites) is WORSE liveness than the WPF trigger it replaces — a desktop is
/// opened daily as a matter of course, a server console is not. That is an operational fact about who runs
/// the host, not a code defect, and no guard here can substitute for someone actually starting the
/// process.</para></summary>
public sealed class StartupJobsHostedService : IHostedService
{
    private readonly IBackupService _backup;
    private readonly IExportHubService _exportHub;
    private readonly IStandupArchiveService _standupArchive;
    private readonly ITaskListArchiveService _taskListArchive;
    private readonly IAppConfig _config;
    private readonly IHostEnvironment _environment;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StartupJobsHostedService> _logger;

    public StartupJobsHostedService(
        IBackupService backup, IExportHubService exportHub, IStandupArchiveService standupArchive,
        ITaskListArchiveService taskListArchive, IAppConfig config, IHostEnvironment environment,
        IHostApplicationLifetime lifetime, ILogger<StartupJobsHostedService> logger)
    {
        _backup = backup;
        _exportHub = exportHub;
        _standupArchive = standupArchive;
        _taskListArchive = taskListArchive;
        _config = config;
        _environment = environment;
        _lifetime = lifetime;
        _logger = logger;
    }

    // Registration stays UNCONDITIONAL in Program.cs — including in the "Testing" environment both
    // ApiFactory and SignalRTestFactory run under — so ValidateOnBuild still exercises this service's DI
    // graph in every test host (a captive/missing dependency here fails loudly, same as everywhere else).
    // What is gated is EXECUTION: ~500 ApiTests spin up a host each, and none of them may write a backup
    // or an archive file into a test's temp root. IHostEnvironment.IsEnvironment("Testing") is the same
    // gate both factories already set (ApiFactory.cs:112, SignalRTestFactory.cs:65).
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_environment.IsEnvironment("Testing")) return Task.CompletedTask;

        // Run after the host has FULLY started (past Kestrel binding the port), never during startup --
        // deploy-local.bat serves the Angular UI from this same process, and a slow export backfill over a
        // network share must not delay the app becoming reachable. Fire-and-forget: StartAsync itself
        // returns immediately, matching the existing Task.Run precedent for background work in this
        // codebase (SettingsEndpoints.cs's retention route).
        _lifetime.ApplicationStarted.Register(() => _ = RunJobsAsync());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunJobsAsync()
    {
        // BK-03, App.xaml.cs:63: unconditional attempt every run. AutoBackupIfDueAsync is itself the
        // guard — disabled flag / unset folder / already-backed-up-today all resolve to a false no-op, so
        // N restarts on the same day take at most one backup (BackupService.cs:59-69).
        try { await _backup.AutoBackupIfDueAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Startup auto-backup failed."); }

        // App.xaml.cs:76-106: mutually exclusive branching, preserved verbatim. When a structured export
        // root is configured, the per-team hub (job 2) supersedes the legacy flat archives; only when NO
        // root is set do the legacy per-org backfills (jobs 3/4) run. Each per-output-file write is its
        // own File.Exists guard, so re-running this on every restart only ever (re)writes what is missing.
        if (!string.IsNullOrWhiteSpace(_config.ExportRoot1Path) ||
            !string.IsNullOrWhiteSpace(_config.ExportRoot2Path))
        {
            try { await _exportHub.BackfillAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Startup structured export backfill failed."); }
        }
        else
        {
            try { await _standupArchive.BackfillMissingWeeksAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Startup standup archive backfill failed."); }

            try { await _taskListArchive.BackfillMissingMonthsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Startup task list archive backfill failed."); }
        }
    }
}
