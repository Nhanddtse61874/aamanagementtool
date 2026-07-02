using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace TimesheetApp.Tests.Views;

// P19: Palette.Light.xaml and Palette.Dark.xaml MUST define exactly the same key set — a key missing from
// Dark would leave a {DynamicResource} unresolved (a light patch / no color in dark mode). This is the
// cheap, high-value guard against palette drift; the render-crash class itself is theme-independent
// (structural) and already covered by the light render tests.
[Collection("WpfSta")]
public sealed class PaletteParityTests
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

    private static ResourceDictionary Load(string name)
    {
        _ = Application.Current ?? new Application();
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/TimesheetApp;component/Views/Theme/{name}", UriKind.Absolute),
        };
    }

    [Fact]
    public void Light_and_Dark_define_the_same_keys_as_brushes_or_colors()
    {
        RunSta(() =>
        {
            var light = Load("Palette.Light.xaml");
            var dark = Load("Palette.Dark.xaml");

            var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToList();
            var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToList();

            Assert.NotEmpty(lightKeys);
            Assert.Equal(lightKeys, darkKeys);   // identical key sets — no missing/extra in either theme

            foreach (var key in light.Keys.Cast<object>())
            {
                Assert.True(light[key] is Brush or Color, $"Light[{key}] is not a Brush/Color");
                Assert.True(dark[key] is Brush or Color, $"Dark[{key}] is not a Brush/Color");
            }
        });
    }
}
