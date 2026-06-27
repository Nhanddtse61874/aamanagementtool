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

        // The SelectUserDialog (XC-07) may be shown modally BEFORE MainWindow exists. With the default
        // OnLastWindowClose, closing/cancelling that dialog would shut the app down before the main
        // window appears. Hold shutdown explicit during startup; switch to main-window-bound after Show().
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var sc = new ServiceCollection();

        // Config + connection seam. SqliteConnectionFactory + JsonAppConfig resolve the DB path
        // from IAppConfig (JsonAppConfig() default ctor -> %APPDATA%\TimesheetApp\appsettings.json,
        // default DB under %USERPROFILE%\Documents\TimesheetApp\timesheet.db on first run).
        sc.AddSingleton<IAppConfig, JsonAppConfig>();
        sc.AddSingleton<IClock, SystemClock>();
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
        sc.AddSingleton<ITimeLogService, TimeLogService>();
        sc.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
        sc.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        sc.AddSingleton<IReportAggregator, ReportAggregator>(); // pure roll-up, stateless
        sc.AddSingleton<IExportService, ExportService>();       // headless export (EXP-01..04)
        // P7 Daily Report (standup) — repo + orchestration + weekly markdown archive.
        sc.AddSingleton<IStandupRepository, StandupRepository>();
        sc.AddSingleton<IStandupService, StandupService>();
        sc.AddSingleton<IStandupArchiveService, StandupArchiveService>();
        // P8 Task List — new repos.
        sc.AddSingleton<ITagRepository, TagRepository>();
        sc.AddSingleton<IPcaContactRepository, PcaContactRepository>();
        sc.AddSingleton<IHolidayRepository, HolidayRepository>();
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

        Services = sc.BuildServiceProvider();

        // One-time bootstrap BEFORE the first window: schema + migrations + DEFAULT seed.
        await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // DR-09: back up any completed week that has standup data but no markdown archive yet
        // (desktop app has no scheduler; runs lazily on each startup). Best-effort, never blocks startup.
        try { await Services.GetRequiredService<IStandupArchiveService>().BackfillMissingWeeksAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Standup archive backfill failed: {ex.Message}"); }

        // TL-09: back up any completed month that has task-list data but no markdown archive yet.
        // Best-effort, never blocks startup (mirrors standup backfill above).
        try { await Services.GetRequiredService<ITaskListArchiveService>().BackfillMissingMonthsAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Task list archive backfill failed: {ex.Message}"); }

        // FIX C1 (DATA-03/TS-02): on a fresh DB the seeded DefaultTasks have no matching Tasks row
        // under the hidden DEFAULT request, so they never appear as Timesheet rows. SyncAsync was
        // only invoked from SettingsViewModel, so it never ran at startup. Run it here AFTER init
        // commits — SyncAsync opens its own connections/transactions, so App-level placement is
        // correct (it must not run inside the initializer's open transaction).
        await Services.GetRequiredService<IDefaultTaskSyncService>().SyncAsync();

        // Shell startup: resolve MainViewModel and run its InitializeAsync (current-user resolution +
        // XC-08 conflict scan + best-effort tab loads). The SelectUserDialog is shown from this View/App
        // layer via the injected selector — the VM stays WPF-free (spec §5/§6, XC-07).
        var mainVm = Services.GetRequiredService<MainViewModel>();
        await mainVm.InitializeAsync(ShowSelectUserDialog);

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

    // View-layer picker passed to MainViewModel on NeedsSelection. Returns the chosen user, or null
    // when the user cancels.
    private static User? ShowSelectUserDialog(IReadOnlyList<User> activeUsers)
    {
        // Pass the user repository so the dialog can create a user inline, and prefill the new-name
        // box with the Windows account so an unmapped joiner can just click OK.
        var dialog = new SelectUserDialog(
            activeUsers, Services.GetRequiredService<IUserRepository>(), Environment.UserName);
        return dialog.ShowDialog() == true ? dialog.SelectedUser : null;
    }
}
