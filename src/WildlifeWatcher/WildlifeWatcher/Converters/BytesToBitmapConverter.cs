using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace WildlifeWatcher.Converters;

[ValueConversion(typeof(byte[]), typeof(BitmapSource))]
public class BytesToBitmapConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        var bitmap = BitmapFrame.Create(
            new MemoryStream(bytes),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
