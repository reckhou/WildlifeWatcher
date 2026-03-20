using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WildlifeWatcher.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
