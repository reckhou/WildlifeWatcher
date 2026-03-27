using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WildlifeWatcher.Converters;

/// <summary>
/// Returns Visible when the bound double value is greater than or equal to the threshold
/// supplied as ConverterParameter (string or double). Returns Collapsed otherwise.
/// Usage: Visibility="{Binding ActualHeight, RelativeSource=...,
///            Converter={StaticResource HeightThresholdConverter}, ConverterParameter=75}"
/// </summary>
[ValueConversion(typeof(double), typeof(Visibility))]
public class HeightThresholdToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double height) return Visibility.Collapsed;
        double threshold = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 75.0;
        return height >= threshold ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
