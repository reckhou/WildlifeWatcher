using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WildlifeWatcher.Converters;

[ValueConversion(typeof(int), typeof(Visibility))]
public class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
