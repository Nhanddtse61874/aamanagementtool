using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Two-way equality between a string property and a ConverterParameter, for binding a group of
/// nav RadioButtons to a single <c>ActiveView</c> string. Convert: value == param. ConvertBack:
/// a checked button writes its param back; an unchecked one is ignored (Binding.DoNothing).
/// </summary>
public sealed class StringMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter ?? Binding.DoNothing : Binding.DoNothing;
}

/// <summary>Visible when the bound string equals the ConverterParameter, else Collapsed.
/// Drives which content panel shows for the current <c>ActiveView</c>.</summary>
public sealed class StringMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
