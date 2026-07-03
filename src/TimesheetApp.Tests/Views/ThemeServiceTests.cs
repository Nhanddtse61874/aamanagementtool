using System;
using System.Linq;
using System.Threading;
using System.Windows;
using TimesheetApp.Services;
using Xunit;

namespace TimesheetApp.Tests.Views;

// P19: ThemeService swaps the palette ResourceDictionary at runtime (the core of the live light/dark
// switch). Runs on an STA thread with a real Application so MergedDictionaries behaves as in the app.
[Collection("WpfSta")]
public sealed class ThemeServiceTests
{
    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { captured = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw captured;
    }

    private static bool HasPalette(Application app, string name) =>
        app.Resources.MergedDictionaries.Any(d => (d.Source?.OriginalString ?? "").Contains(name));

    [Fact]
    public void Apply_swaps_palette_dictionary_and_flips_IsDark()
    {
        RunSta(() =>
        {
            var app = Application.Current ?? new Application();
            foreach (var d in app.Resources.MergedDictionaries
                         .Where(d => (d.Source?.OriginalString ?? "").Contains("Palette.")).ToList())
                app.Resources.MergedDictionaries.Remove(d);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Light.xaml", UriKind.Absolute),
            });

            var svc = new ThemeService();

            svc.Apply(true);
            Assert.True(svc.IsDark);
            Assert.True(HasPalette(app, "Palette.Dark"));
            Assert.False(HasPalette(app, "Palette.Light"));

            svc.Apply(false);
            Assert.False(svc.IsDark);
            Assert.True(HasPalette(app, "Palette.Light"));
            Assert.False(HasPalette(app, "Palette.Dark"));
        });
    }
}
