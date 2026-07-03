using System.Windows;

namespace TimesheetApp.Services;

// P19: swaps the palette dictionary (Palette.Light.xaml <-> Palette.Dark.xaml) at index in the app's
// MergedDictionaries. Palette keys are consumed via DynamicResource, so replacing the dictionary
// re-resolves every themed brush live. WPF-only; a no-op when there is no Application (unit tests).
public sealed class ThemeService : IThemeService
{
    private const string LightUri = "pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Light.xaml";
    private const string DarkUri = "pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Dark.xaml";

    public bool IsDark { get; private set; }

    public void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;   // headless / unit-test context

        var dicts = app.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = new System.Uri(dark ? DarkUri : LightUri) };

        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("Palette.Light") || src.Contains("Palette.Dark"))
            {
                dicts[i] = replacement;   // swap in place -> DynamicResource consumers re-resolve
                IsDark = dark;
                return;
            }
        }

        // No palette merged yet (unexpected) — add it so the app is at least themed.
        dicts.Insert(0, replacement);
        IsDark = dark;
    }
}
