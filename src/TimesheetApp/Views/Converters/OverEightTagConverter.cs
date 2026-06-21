using System.Globalization;
using System.Windows.Data;

namespace TimesheetApp.Views.Converters;

public sealed class OverEightTagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is decimal d && d > 8m;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
