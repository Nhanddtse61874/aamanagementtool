using System.Collections.Generic;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Dialogs;

namespace TimesheetApp;

// DI composition root (architecture spec §6). Bare ServiceCollection (no Generic Host).
// Repos + services are singletons (stateless — a short connection is opened per method, nothing held);
// ViewModels are transient. IDatabaseInitializer.InitializeAsync() runs once before any window.
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UAT safety net: surface any unhandled UI-thread exception as a readable dialog instead of a
        // silent crash (fire-and-forget async-void callbacks have nowhere else to report). Keep the app
        // alive so a single failed action doesn't kill the session.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message + "\n\n" + args.Exception.GetType().FullName,
                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // A modal shown during startup, BEFORE MainWindow exists, would (under the default
        // OnLastWindowClose) shut the app down when dismissed. Hold shutdown explicit during startup;
        // switch to main-window-bound after Show().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var sc = new ServiceCollection();
        ConfigureServices(sc);

        Services = sc.BuildServiceProvider();

        // P19: apply the saved theme (light/dark) BEFORE any window renders — swaps the palette dictionary;
        // DynamicResource consumers pick it up. Independent of the DB, so it runs first.
        Services.GetRequiredService<IThemeService>().Apply(Services.GetRequiredService<IAppConfig>().IsDarkMode);

        // One-time bootstrap BEFORE the first window: schema + migrations + DEFAULT seed.
        await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // P10 (TM-02/TM-09, architecture §6b): create the team(s) the rest of startup depends on.
        // Runs AFTER InitializeAsync (needs the v8 schema) and BEFORE the archive backfills +
        // DefaultTaskSync (per-team sync iterates active teams, so teams must exist first). Idempotent
        // (no-op once any team exists) and backup-first. Teams are required for a usable app, so unlike
        // the best-effort backfills below this is NOT swallowed — a bootstrap failure surfaces like
        // InitializeAsync does (via DispatcherUnhandledException) rather than launching a team-less shell.
        await Services.GetRequiredService<ITeamBootstrapService>().EnsureBootstrappedAsync();

        // BK-03: once-per-day local DB backup on startup when auto-backup is enabled. Best-effort,
        // never blocks startup.
        try { await Services.GetRequiredService<IBackupService>().AutoBackupIfDueAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Auto-backup failed: {ex.Message}"); }

        // FIX C1 (DATA-03/TS-02): on a fresh DB the seeded DefaultTasks have no matching Tasks row
        // under the hidden DEFAULT request, so they never appear as Timesheet rows. SyncAsync was
        // only invoked from SettingsViewModel, so it never ran at startup. Run it here AFTER init
        // commits — SyncAsync opens its own connections/transactions, so App-level placement is
        // correct (it must not run inside the initializer's open transaction).
        await Services.GetRequiredService<IDefaultTaskSyncService>().SyncAsync();

        // P11 (EX-06): backfill completed weeks/months on startup (desktop app has no scheduler; runs
        // lazily on each startup). Best-effort, never blocks startup. Runs AFTER EnsureBootstrappedAsync
        // + DefaultTaskSync so teams and their synced default tasks exist before any export is built.
        // When an export root is configured the structured per-team hub supersedes the legacy flat
        // archives (EX-06); with no root set we keep the legacy flat backfills so behavior is unchanged
        // for users who haven't opted in.
        var config = Services.GetRequiredService<IAppConfig>();
        if (!string.IsNullOrWhiteSpace(config.ExportRoot1Path) ||
            !string.IsNullOrWhiteSpace(config.ExportRoot2Path))
        {
            try { await Services.GetRequiredService<IExportHubService>().BackfillAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Structured export backfill failed: {ex.Message}"); }

            // P12 (BLOCKER-3): retention runs LAST, only inside the root-configured block (a no-root
            // machine never prunes), AFTER the export backfill (so the just-written markdown is current)
            // and therefore after EnsureBootstrapped + DefaultTaskSync. Opt-in (RetentionEnabled) and
            // best-effort — a failed/aborted run must never block startup. The conflict-copy abort and
            // the archive→verify-snapshot→delete guards live inside EnsureRetentionAsync.
            if (config.RetentionEnabled)
            {
                try { await Services.GetRequiredService<IRetentionService>().EnsureRetentionAsync(); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Retention run failed: {ex.Message}"); }
            }
        }
        else
        {
            // DR-09: back up any completed week that has standup data but no markdown archive yet.
            try { await Services.GetRequiredService<IStandupArchiveService>().BackfillMissingWeeksAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Standup archive backfill failed: {ex.Message}"); }

            // TL-09: back up any completed month that has task-list data but no markdown archive yet.
            try { await Services.GetRequiredService<ITaskListArchiveService>().BackfillMissingMonthsAsync(); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Task list archive backfill failed: {ex.Message}"); }
        }

        // Shell startup: resolve MainViewModel and run its InitializeAsync (current-user resolution +
        // XC-08 conflict scan + best-effort tab loads).
        var mainVm = Services.GetRequiredService<MainViewModel>();
        await mainVm.InitializeAsync();

        // XC-09: surface lingering-journal warnings on the shell banner. Marshalled onto the UI thread
        // here (the sink stays System.Windows-free). The singleton sink outlives the VM, so this is safe.
        var journalSink = Services.GetRequiredService<UiJournalWarningSink>();
        journalSink.WarningRaised += (_, _) =>
            Dispatcher.Invoke(() => mainVm.JournalWarning = journalSink.LatestWarning);

        MainWindow = new MainWindow { DataContext = mainVm };
        MainWindow.Show();

        // Window is up: bind shutdown to it so closing the main window exits the app normally.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    // DI composition (architecture §6). Extracted from OnStartup so it can be exercised by a
    // DI-resolution test (W9) WITHOUT duplicating the registration block — the test builds this exact
    // graph and asserts every VM/service resolves. Pure registration: no DB/window side effects.
    internal static void ConfigureServices(ServiceCollection sc)
    {
        // Config + connection seam. SqliteConnectionFactory + JsonAppConfig resolve the DB path
        // from IAppConfig (JsonAppConfig() default ctor -> %APPDATA%\TimesheetApp\appsettings.json,
        // default DB under %USERPROFILE%\Documents\TimesheetApp\timesheet.db on first run).
        sc.AddSingleton<IAppConfig, JsonAppConfig>();
        sc.AddSingleton<IClock, SystemClock>();
        sc.AddSingleton<IThemeService, ThemeService>();   // P19: runtime light/dark palette swap
        sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>();
        // Cross-tab live sync bus: producers Send(DataChangedMessage), consumer VMs reload.
        sc.AddSingleton<CommunityToolkit.Mvvm.Messaging.IMessenger>(
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default);
        // XC-09 observability seam: bulk-write services route lingering-journal warnings here
        // (System.Windows-free; never swallowed). UiJournalWarningSink wraps the trace sink so the
        // warning is BOTH traced and surfaced to the shell banner (event wired below after the VM exists).
        sc.AddSingleton<TraceJournalWarningSink>();
        sc.AddSingleton<UiJournalWarningSink>(sp =>
            new UiJournalWarningSink(sp.GetRequiredService<TraceJournalWarningSink>()));
        sc.AddSingleton<IJournalWarningSink>(sp => sp.GetRequiredService<UiJournalWarningSink>());

        // Repositories (singletons — stateless; connection opened per method).
        sc.AddSingleton<IUserRepository, UserRepository>();
        sc.AddSingleton<IBacklogRepository, BacklogRepository>();
        sc.AddSingleton<ITaskRepository, TaskRepository>();
        sc.AddSingleton<ITimeLogRepository, TimeLogRepository>();
        sc.AddSingleton<ISettingsRepository, SettingsRepository>();
        sc.AddSingleton<ITaskTemplateRepository, TaskTemplateRepository>();
        sc.AddSingleton<IDefaultTaskRepository, DefaultTaskRepository>();

        // Services (singletons).
        sc.AddSingleton<ISmartInputService, SmartInputService>();
        sc.AddSingleton<ICurrentUserService, CurrentUserService>();
        sc.AddSingleton<IDbBackupHelper, DbBackupHelper>();
        sc.AddSingleton<IBackupService, BackupService>(); // P9: user-controlled local backup/restore
        sc.AddSingleton<ITimeLogService, TimeLogService>();
        sc.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
        sc.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        sc.AddSingleton<IReportAggregator, ReportAggregator>(); // pure roll-up, stateless
        sc.AddSingleton<IExportService, ExportService>();       // headless export (EXP-01..04)
        // P11 (EX-02/05/06/07): structured per-team export hub + path sanitizer.
        sc.AddSingleton<IPathSanitizer, PathSanitizer>();
        sc.AddSingleton<IExportHubService, ExportHubService>();
        // P14 (SP-01): verifies the SharePoint/network export destination before "Export now".
        sc.AddSingleton<ISharePointDestinationValidator, SharePointDestinationValidator>();
        // P12 (RT-03/RT-05): retention/prune core + the archive-before-prune seam. Singletons next to
        // the export services. PruneArchiver writes the per-team markdown + the never-auto-pruned .db
        // snapshot; RetentionService re-verifies that snapshot before any delete.
        sc.AddSingleton<IPruneArchiver, PruneArchiver>();
        sc.AddSingleton<IRetentionService, RetentionService>();
        // P7 Daily Report (standup) — repo + orchestration + weekly markdown archive.
        sc.AddSingleton<IStandupRepository, StandupRepository>();
        sc.AddSingleton<IStandupService, StandupService>();
        sc.AddSingleton<IStandupArchiveService, StandupArchiveService>();
        // P8 Task List — new repos.
        sc.AddSingleton<ITagRepository, TagRepository>();
        sc.AddSingleton<IBacklogContinuationService, BacklogContinuationService>();   // P20: continue to next month
        sc.AddSingleton<IPcaContactRepository, PcaContactRepository>();
        sc.AddSingleton<IHolidayRepository, HolidayRepository>();
        // P10 Multi-Team — team repo + current-team context + bootstrap (W9 wiring). NOTE: do NOT add
        // a second Func<int> for the active team id — it would clobber the existing user-id provider
        // (last-wins). Services that need the team id inject ICurrentTeamService directly.
        sc.AddSingleton<ITeamRepository, TeamRepository>();
        sc.AddSingleton<ICurrentTeamService, CurrentTeamService>();
        sc.AddSingleton<ITeamBootstrapService, TeamBootstrapService>();
        // P8 Task List — new services.
        sc.AddSingleton<IWorkingDayCalculator, WorkingDayCalculator>();
        sc.AddSingleton<IScheduleStateService, ScheduleStateService>();
        sc.AddSingleton<ITaskListArchiveService, TaskListArchiveService>();

        // Current-user-id provider (Func<int>) per plan/spec: TimesheetViewModel persists cells for
        // the logged-in user. Resolution defers to ICurrentUserService.Current (set by login flow);
        // 0 until a user is resolved. This is the single, testable seam — VMs take Func<int>, not a
        // captured int, so the id stays live after login without re-resolving the VM.
        sc.AddSingleton<Func<int>>(sp =>
        {
            var currentUser = sp.GetRequiredService<ICurrentUserService>();
            return () => currentUser.Current?.Id ?? 0;
        });

        // ViewModels (transient).
        sc.AddTransient<MainViewModel>();
        sc.AddTransient<TimesheetViewModel>();
        sc.AddTransient<BacklogsViewModel>();
        sc.AddTransient<UsersViewModel>();
        sc.AddTransient<ReportsViewModel>();
        sc.AddTransient<SettingsViewModel>();
        sc.AddTransient<DailyReportViewModel>();
        sc.AddTransient<TaskListViewModel>();
    }
}
