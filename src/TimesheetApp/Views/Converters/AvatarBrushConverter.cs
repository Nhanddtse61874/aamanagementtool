using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Picks a stable avatar background from the design palette by hashing the name,
/// mirroring the mockup's per-user colours (avatars[i % len]). A manual char sum is
/// used (not string.GetHashCode, which is randomised per process) so a user keeps
/// the same colour across restarts.
/// </summary>
public sealed class AvatarBrushConverter : IValueConverter
{
    // Design palette (Timesheet Tool.dc.html): const avatars = [...].
    private static readonly Brush[] Palette =
    {
        Freeze("#2563EB"), Freeze("#0891B2"), Freeze("#7C3AED"), Freeze("#DB2777"), Freeze("#16A34A"),
    };

    private static Brush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        if (string.IsNullOrWhiteSpace(s))
            return Palette[0];

        var hash = 0;
        foreach (var c in s.Trim())
            hash += c;
        return Palette[hash % Palette.Length];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
