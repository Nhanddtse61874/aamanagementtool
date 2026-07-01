using System.Windows;
using Moq;
using TimesheetApp.Config;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;
using TimesheetApp.ViewModels;
using TimesheetApp.Views.Converters;
using TimesheetApp.Views.Tabs;
using Xunit;

namespace TimesheetApp.Tests.Views;

// ISSUE 4 regression: the Settings team-membership overlay bound a <Run Text="{Binding TeamName}"/>,
// and Run.Text binds TwoWay by default. TeamName is a read-only ({ get; private init; }) property, so
// WPF threw InvalidOperationException ("A TwoWay or OneWayToSource binding cannot work on the read-only
// property 'TeamName' ...") the moment the overlay rendered — surfacing as the global error dialog when
// a user clicked "Members". This test renders the real SettingsTab with the overlay open on an STA
// thread so any future read-only/TwoWay binding regression in that overlay fails CI instead of runtime.
[Collection("WpfSta")]
public sealed class SettingsMembershipOverlayLoadTests
{
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    // Mirror App.xaml: merge Theme.xaml + register every converter the SettingsTab XAML references so
    // each StaticResource resolves during instantiation/layout.
    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application();
        if (!app.Resources.Contains("ToolbarGhostToggle"))
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/TimesheetApp;component/Views/Theme/Theme.xaml", UriKind.Absolute),
            });
        }
        app.Resources["OverEightTag"] = new OverEightTagConverter();
        app.Resources["NullToCollapsedConverter"] = new NullToCollapsedConverter();
        app.Resources["BoolToVisibleConverter"] = new BoolToVisibilityConverter();
        app.Resources["ActiveStatusConverter"] = new ActiveStatusConverter();
        app.Resources["StringMatch"] = new StringMatchConverter();
        app.Resources["StringMatchToVisibility"] = new StringMatchToVisibilityConverter();
        app.Resources["Initial"] = new InitialConverter();
        app.Resources["AvatarBrush"] = new AvatarBrushConverter();
        app.Resources["DateOnly"] = new DateOnlyConverter();
        app.Resources["HexToBrush"] = new HexToBrushConverter();
    }

    private static SettingsViewModel BuildVm()
    {
        var config = new Mock<IAppConfig>();
        var settings = new Mock<ISettingsRepository>();
        settings.Setup(s => s.GetAsync(ReportsViewModel.NDaysKey)).ReturnsAsync("3");
        var templates = new Mock<ITaskTemplateRepository>();
        templates.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<TaskTemplate>());
        var tags = new Mock<ITagRepository>();
        tags.Setup(t => t.GetAllAsync()).ReturnsAsync(Array.Empty<Tag>());
        var pca = new Mock<IPcaContactRepository>();
        pca.Setup(p => p.GetAllAsync()).ReturnsAsync(Array.Empty<PcaContact>());
        var holidays = new Mock<IHolidayRepository>();
        holidays.Setup(h => h.GetForMonthAsync(It.IsAny<int>(), It.IsAny<int>()))
                .ReturnsAsync(Array.Empty<Holiday>());
        var teams = new Mock<ITeamRepository>();
        teams.Setup(t => t.GetAllAsync()).ReturnsAsync(new[] { new Team(7, "Squad", true, DateTimeOffset.UtcNow) });
        teams.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(new Team(7, "Squad", true, DateTimeOffset.UtcNow));
        teams.Setup(t => t.GetUserIdsForTeamAsync(7)).ReturnsAsync(new[] { 2 });
        var users = new Mock<IUserRepository>();
        users.Setup(u => u.GetActiveAsync()).ReturnsAsync(new[]
        {
            new User(1, "Alice", null, true),
            new User(2, "Bob", null, true),
        });
        return new SettingsViewModel(config.Object, settings.Object, templates.Object,
            Mock.Of<IDefaultTaskSyncService>(), tags.Object, pca.Object, holidays.Object,
            Mock.Of<IBackupService>(), teams.Object, users.Object);
    }

    [Fact]
    public void MembershipOverlay_renders_without_readonly_binding_crash()
    {
        RunSta(() =>
        {
            EnsureAppResources();

            var vm = BuildVm();
            vm.LoadAsync().GetAwaiter().GetResult();
            vm.BeginEditMembersCommand.ExecuteAsync(7).GetAwaiter().GetResult();
            Assert.NotNull(vm.MembershipEditor);

            // Rendering the open overlay would throw InvalidOperationException on the read-only TeamName
            // binding if Run.Text reverted to its default TwoWay mode.
            var win = new Window
            {
                Content = new SettingsTab { DataContext = vm },
                Width = 800,
                Height = 600,
            };
            win.Show();
            win.UpdateLayout();
            win.Close();
        });
    }
}
