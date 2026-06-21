using System.Globalization;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

/// <summary>
/// Converts a bool IsActive to the display string "Active" or "Inactive".
/// Used in UsersTab to display user status in the Status column (USR-01).
/// </summary>
public sealed class ActiveStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Active" : "Inactive";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
