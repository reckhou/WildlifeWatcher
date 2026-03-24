using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WildlifeWatcher.Models;

namespace WildlifeWatcher.ViewModels;

public partial class CaptureCardViewModel : ObservableObject
{
    public CaptureRecord Record          { get; }
    public string        ImagePath       => Record.ImageFilePath;
    public string        TimeLabel       => Record.CapturedAt.ToString("dd MMM  HH:mm:ss");
    public string        ConfidenceLabel => $"{Record.ConfidenceScore:P0}";

    // Preserved for CaptureDetailDialog path-based usage
    public string DisplayImagePath { get; }

    [ObservableProperty] private bool _isSelected;

    public CaptureCardViewModel(CaptureRecord record)
    {
        Record = record;
        DisplayImagePath = File.Exists(record.ImageFilePath)
            ? record.ImageFilePath
            : record.AnnotatedImageFilePath is { Length: > 0 } p && File.Exists(p) ? p : record.ImageFilePath;
    }

    /// <summary>
    /// Returns a cropped BitmapSource zoomed to the source POI region when bounding box data
    /// is available, otherwise returns the full annotated/original image. Used for gallery thumbnails.
    /// </summary>
    public BitmapSource? ThumbnailSource
    {
        get
        {
            var path = DisplayImagePath;
            if (!File.Exists(path)) return null;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource   = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (!Record.PoiNLeft.HasValue)
                    bmp.DecodePixelWidth = 200;
                bmp.EndInit();
                bmp.Freeze();

                if (!Record.PoiNLeft.HasValue) return bmp;

                int x = (int)(Record.PoiNLeft.Value   * bmp.PixelWidth);
                int y = (int)(Record.PoiNTop!.Value   * bmp.PixelHeight);
                int w = (int)(Record.PoiNWidth!.Value  * bmp.PixelWidth);
                int h = (int)(Record.PoiNHeight!.Value * bmp.PixelHeight);

                x = Math.Max(0, x); y = Math.Max(0, y);
                w = Math.Min(w, bmp.PixelWidth  - x);
                h = Math.Min(h, bmp.PixelHeight - y);
                if (w <= 0 || h <= 0) return bmp;

                var cropped = new CroppedBitmap(bmp, new System.Windows.Int32Rect(x, y, w, h));
                cropped.Freeze();
                return cropped;
            }
            catch { return null; }
        }
    }
}
