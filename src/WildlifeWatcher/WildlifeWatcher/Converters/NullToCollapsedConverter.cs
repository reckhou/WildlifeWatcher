using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WildlifeWatcher.Converters;

/// <summary>
/// Converts nullable value types (structs) to Visibility.
/// null → Collapsed, non-null → Visible.
/// For nullable strings use NullOrEmptyToCollapsedConverter instead.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
