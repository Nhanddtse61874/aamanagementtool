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

        var sc = new ServiceCollection();

        // Config + connection seam. SqliteConnectionFactory + JsonAppConfig resolve the DB path
        // from IAppConfig (JsonAppConfig() default ctor -> %APPDATA%\TimesheetApp\appsettings.json,
        // default DB under %USERPROFILE%\Documents\TimesheetApp\timesheet.db on first run).
        sc.AddSingleton<IAppConfig, JsonAppConfig>();
        sc.AddSingleton<IClock, SystemClock>();
        sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>();

        // Repositories (singletons — stateless; connection opened per method).
        sc.AddSingleton<IUserRepository, UserRepository>();
        sc.AddSingleton<IRequestRepository, RequestRepository>();
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
        sc.AddTransient<RequestsViewModel>();
        sc.AddTransient<UsersViewModel>();
        sc.AddTransient<ReportsViewModel>();
        sc.AddTransient<SettingsViewModel>();

        Services = sc.BuildServiceProvider();

        // One-time bootstrap BEFORE the first window: schema + migrations + DEFAULT seed.
        await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // Shell startup: resolve MainViewModel and run its InitializeAsync (current-user resolution +
        // XC-08 conflict scan + best-effort tab loads). The SelectUserDialog is shown from this View/App
        // layer via the injected selector — the VM stays WPF-free (spec §5/§6, XC-07).
        var mainVm = Services.GetRequiredService<MainViewModel>();
        await mainVm.InitializeAsync(ShowSelectUserDialog);

        MainWindow = new MainWindow { DataContext = mainVm };
        MainWindow.Show();
    }

    // View-layer picker passed to MainViewModel on NeedsSelection. Returns the chosen user, or null
    // when the user cancels.
    private static User? ShowSelectUserDialog(IReadOnlyList<User> activeUsers)
    {
        var dialog = new SelectUserDialog(activeUsers);
        return dialog.ShowDialog() == true ? dialog.SelectedUser : null;
    }
}
