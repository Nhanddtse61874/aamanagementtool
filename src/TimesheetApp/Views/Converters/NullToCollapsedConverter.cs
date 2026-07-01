using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Returns Visible when the value is non-null (and, for strings, non-blank), Collapsed otherwise.
/// Used to show/hide the editor overlay when BacklogsViewModel.Editor != null, and to hide a tag
/// chip's icon slot when the tag has no icon (an empty "" would otherwise leave a phantom gap).
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
