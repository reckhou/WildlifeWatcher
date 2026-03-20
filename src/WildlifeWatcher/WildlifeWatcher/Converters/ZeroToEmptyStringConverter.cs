using System.Globalization;
using System.Windows.Data;

namespace WildlifeWatcher.Converters;

[ValueConversion(typeof(int), typeof(string))]
public class ZeroToEmptyStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i == 0 ? string.Empty : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
