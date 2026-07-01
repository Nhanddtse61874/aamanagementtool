using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Returns Visible when true, Collapsed when false. Pass ConverterParameter="Invert" to flip
/// (Visible when false) — used to hide a display element while its inline editor is showing.
/// Used to show the "(removed)" label on task rows flagged IsRemoved=true.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
}
