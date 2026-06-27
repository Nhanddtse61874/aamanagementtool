using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Turns a hex color string (e.g. "#0F766E") into a frozen SolidColorBrush for tag chip
/// backgrounds (TAG-02). User-entered hex is untrusted, so any parse failure falls back to a
/// neutral brush instead of throwing — an invalid color must never crash the UI thread.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    // Neutral fallback (theme BadgeGrayBg) used for blank / malformed input.
    private static readonly Brush Fallback = Freeze("#EEF1F5");

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value?.ToString();
        if (string.IsNullOrWhiteSpace(hex))
            return Fallback;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex.Trim())!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            // FormatException / InvalidCastException / null on garbage hex — stay neutral, never throw.
            return Fallback;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
