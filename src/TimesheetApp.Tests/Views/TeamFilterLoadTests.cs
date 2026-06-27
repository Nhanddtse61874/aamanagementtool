using System.Windows;
using System.Windows.Controls.Primitives;
using TimesheetApp.Views.Controls;
using TimesheetApp.Views.Converters;
using Xunit;

namespace TimesheetApp.Tests.Views;

// C1 regression: TeamFilter.xaml applied a Button-targeted style to a ToggleButton, which WPF rejects
// with InvalidOperationException the moment the control loads — breaking the multi-team filter on all
// four screens. This test instantiates the real control (with the app's merged resources) on an STA
// thread so any future style/TargetType mismatch on TeamFilter fails CI instead of only at runtime.
public sealed class TeamFilterLoadTests
{
    // Runs an action on a dedicated STA thread (WPF controls require STA) and surfaces any exception.
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

    // Loads Theme.xaml (brushes + the ToolbarGhostToggle style) and the one converter TeamFilter uses
    // (BoolToVisibleConverter, declared in App.xaml) into Application.Current so every StaticResource
    // reference in TeamFilter.xaml resolves at instantiation.
    private static void EnsureAppResources()
    {
        var app = Application.Current ?? new Application();
        if (app.Resources.Contains("ToolbarGhostToggle")) return;

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/TimesheetApp;component/Views/Theme/Theme.xaml", UriKind.Absolute),
        });
        app.Resources["BoolToVisibleConverter"] = new BoolToVisibilityConverter();
    }

    [Fact]
    public void TeamFilter_instantiates_without_style_target_type_mismatch()
    {
        RunSta(() =>
        {
            EnsureAppResources();

            // Would throw InvalidOperationException ("not applicable to an object of type ...") if the
            // ToggleButton carried a Button-targeted style again.
            var control = new TeamFilter();

            Assert.NotNull(control);
            var toggle = control.FindName("TeamsToggle") as ToggleButton;
            Assert.NotNull(toggle);
            // The resolved style must target ToggleButton (or a base) — the actual C1 invariant.
            Assert.True(toggle!.Style is null || toggle.Style.TargetType.IsAssignableFrom(typeof(ToggleButton)),
                $"TeamsToggle style TargetType {toggle.Style?.TargetType} is not applicable to ToggleButton.");
        });
    }
}
