using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;

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
        sc.AddSingleton<IDefaultTaskRepository, DefaultTaskRepository>();

        // Services (singletons).
        sc.AddSingleton<ISmartInputService, SmartInputService>();
        sc.AddSingleton<ICurrentUserService, CurrentUserService>();
        sc.AddSingleton<IDbBackupHelper, DbBackupHelper>();
        sc.AddSingleton<ITimeLogService, TimeLogService>();
        sc.AddSingleton<IDefaultTaskSyncService, DefaultTaskSyncService>();
        sc.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();

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
        sc.AddTransient<TimesheetViewModel>();

        Services = sc.BuildServiceProvider();

        // One-time bootstrap BEFORE the first window: schema + migrations + DEFAULT seed.
        await Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

        // NOTE: the first window (MainWindow + MainViewModel) is wired in a later phase; those
        // types do not exist yet in P3. The DI container + DB bootstrap above are complete and the
        // TimesheetViewModel is resolvable now (Services.GetRequiredService<TimesheetViewModel>()).
    }
}
