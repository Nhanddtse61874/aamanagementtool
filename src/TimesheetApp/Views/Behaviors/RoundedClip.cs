using System.Windows;
using System.Windows.Media;

namespace TimesheetApp.Views.Behaviors;

/// <summary>
/// Clips a FrameworkElement to a rounded rectangle, kept in sync with its size.
/// WPF's DataGrid has no CornerRadius, so wrapping it in a rounded Border alone
/// leaves the grid's square corners poking out; this clips the grid to match the
/// container's 8px radius (design: tables use border-radius:8px; overflow:hidden).
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip),
            new PropertyMetadata(0d, OnRadiusChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double v) => o.SetValue(RadiusProperty, v);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        fe.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
        {
            fe.SizeChanged += OnSizeChanged;
            UpdateClip(fe);
        }
        else
        {
            fe.Clip = null;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateClip((FrameworkElement)sender);

    private static void UpdateClip(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
    }
}
