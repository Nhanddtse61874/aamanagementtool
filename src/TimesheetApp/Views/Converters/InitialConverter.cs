using System.Globalization;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

/// <summary>First (uppercased) letter of a name, for the round avatar chip. Empty -> "?".</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return string.IsNullOrWhiteSpace(s) ? "?" : s.Trim()[..1].ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
