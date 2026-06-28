using Microsoft.Extensions.DependencyInjection;
using TimesheetApp;
using TimesheetApp.Config;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using Xunit;

namespace TimesheetApp.Tests;

/// <summary>
/// W9 DI-resolution guard: builds the EXACT service graph App.OnStartup uses (via the extracted
/// <see cref="App.ConfigureServices"/>) and asserts every ViewModel + the P10 services resolve with
/// all transitive constructor dependencies registered. This is the regression net for the
/// "forgot to register ITeamRepository/ICurrentTeamService/ITeamBootstrapService" class of bug, and
/// it walks MainViewModel's full ctor graph (incl. the new ITeamRepository dep).
/// </summary>
public sealed class DependencyInjectionTests
{
    // Build the real registration block, then point IAppConfig at a throwaway temp file so the test
    // never reads/writes the developer's real %APPDATA% config. No DB or window is touched — resolving
    // singletons/transients only runs constructors (repos open a connection per METHOD, not in ctor).
    private static ServiceProvider BuildProvider()
    {
        var sc = new ServiceCollection();
        App.ConfigureServices(sc);

        var tempDir = Path.Combine(Path.GetTempPath(), "di-test-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(tempDir, "appsettings.json");
        var dbPath = Path.Combine(tempDir, "timesheet.db");
        sc.AddSingleton<IAppConfig>(new JsonAppConfig(configPath, dbPath));

        return sc.BuildServiceProvider();
    }

    [Fact] // MainViewModel resolves with every transitive ctor dep (incl. ITeamRepository) registered.
    public void MainViewModel_resolves_with_all_dependencies()
    {
        using var sp = BuildProvider();
        var mainVm = sp.GetRequiredService<MainViewModel>();
        Assert.NotNull(mainVm);
    }

    [Theory] // Every ViewModel App registers must resolve (no missing registration / ctor mismatch).
    [InlineData(typeof(MainViewModel))]
    [InlineData(typeof(TimesheetViewModel))]
    [InlineData(typeof(BacklogsViewModel))]
    [InlineData(typeof(UsersViewModel))]
    [InlineData(typeof(ReportsViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    [InlineData(typeof(DailyReportViewModel))]
    [InlineData(typeof(TaskListViewModel))]
    public void Every_view_model_resolves(Type vmType)
    {
        using var sp = BuildProvider();
        Assert.NotNull(sp.GetRequiredService(vmType));
    }

    [Theory] // The P10 services + the services that gained P10 ctor deps must resolve.
    [InlineData(typeof(ITeamRepository))]
    [InlineData(typeof(ICurrentTeamService))]
    [InlineData(typeof(ITeamBootstrapService))]
    [InlineData(typeof(IDefaultTaskSyncService))]
    [InlineData(typeof(ITimeLogService))]
    [InlineData(typeof(IStandupService))]
    [InlineData(typeof(IExportService))]
    [InlineData(typeof(ITaskListArchiveService))]
    [InlineData(typeof(IStandupArchiveService))]
    [InlineData(typeof(IDatabaseInitializer))]
    [InlineData(typeof(IPathSanitizer))]      // P11 (EX-07)
    [InlineData(typeof(IExportHubService))]   // P11 (EX-02/05/06) — walks its full 8-dep ctor graph
    public void P10_services_resolve(Type serviceType)
    {
        using var sp = BuildProvider();
        Assert.NotNull(sp.GetRequiredService(serviceType));
    }

    [Fact] // Regression for the §6a collision: the Func<int> stays the USER-id provider, not a team id.
    // After registering all services, resolving Func<int> must give the current-user-id delegate
    // (it returns 0 when no user is resolved, never throwing) — proving no second Func<int> clobbered it.
    public void Func_int_provider_is_the_current_user_id_provider()
    {
        using var sp = BuildProvider();
        var provider = sp.GetRequiredService<Func<int>>();
        Assert.Equal(0, provider()); // no user resolved -> ICurrentUserService.Current?.Id ?? 0
    }
}
