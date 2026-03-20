using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace WildlifeWatcher.Converters;

[ValueConversion(typeof(string), typeof(BitmapImage))]
public class FilePathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource        = new Uri(path, UriKind.Absolute);
            img.CacheOption      = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth  = 200;
            img.DecodePixelHeight = 200;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
