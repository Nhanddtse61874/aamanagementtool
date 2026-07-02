using System.Windows;
using Moq;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Views.Dialogs;
using Xunit;

namespace TimesheetApp.Tests.Views;

// Guard for the restyled first-run identity dialog: SelectUserDialog moved from native OS chrome to the
// app's themed WindowStyle=None card, so it now resolves StaticResource brushes/styles (Surface, HeaderBg,
// Border, TextPrimary/Secondary, GhostButton) at load. If any of those go missing the XAML throws the
// moment the dialog is constructed — which, being the startup identity gate, would block launch. This
// instantiates the real dialog with the app's merged resources on an STA thread so a missing-resource
// regression fails CI instead of at first run.
[Collection("WpfSta")]
public sealed class SelectUserDialogLoadTests
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

    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application();
        if (app.Resources.Contains("Surface")) return;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Light.xaml", UriKind.Absolute) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/TimesheetApp;component/Views/Theme/Theme.xaml", UriKind.Absolute),
        });
    }

    [Fact]
    public void SelectUserDialog_instantiates_with_themed_chrome()
    {
        RunSta(() =>
        {
            EnsureAppResources();

            var dlg = new SelectUserDialog(Array.Empty<User>(), Mock.Of<IUserRepository>(), prefillName: "DOMAIN\\joe");

            Assert.NotNull(dlg);
            Assert.Equal(WindowStyle.None, dlg.WindowStyle); // themed chrome, not the native OS window
        });
    }
}
