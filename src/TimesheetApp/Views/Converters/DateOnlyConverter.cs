using System.Globalization;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

// DateOnly (VM) <-> DateTime? (DatePicker.SelectedDate). Self-contained for the Reports tab.
public sealed class DateOnlyConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : (DateTime?)null;

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime dt ? DateOnly.FromDateTime(dt) : default(DateOnly);
}
