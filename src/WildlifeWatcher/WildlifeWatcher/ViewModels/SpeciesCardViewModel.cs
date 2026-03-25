using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.ViewModels;

/// <summary>View-model for a species card. Loads its preview image asynchronously to avoid blocking the UI.</summary>
public class SpeciesCardViewModel : ObservableObject
{
    public SpeciesSummary Summary         { get; }
    public int            CaptureCount    { get; }
    public string         PreviewImagePath { get; }
    public string         FirstSeenLabel  { get; }
    public string         LastSeenLabel   { get; }
    public DateTime       LatestCaptureAt { get; }

    // Same pool-starvation guard as CaptureCardViewModel — prevents 20+ simultaneous Task.Run
    // image decodes from backing up the thread pool and leaving below-fold cards blank.
    private static readonly SemaphoreSlim _loadSemaphore =
        new(Math.Max(Environment.ProcessorCount, 4), Math.Max(Environment.ProcessorCount, 4));

    // Async preview image — loads on background thread on first binding access
    private BitmapImage? _previewImage;
    private int _previewImageLoaded;

    public BitmapImage? PreviewImage
    {
        get
        {
            if (!string.IsNullOrEmpty(PreviewImagePath) &&
                Interlocked.CompareExchange(ref _previewImageLoaded, 1, 0) == 0)
                _ = LoadPreviewImageAsync();
            return _previewImage;
        }
    }

    private async Task LoadPreviewImageAsync()
    {
        var path = PreviewImagePath;
        await _loadSemaphore.WaitAsync();
        try
        {
            var source = await Task.Run(() => LoadImageFromDisk(path));
            _previewImage = source;
            OnPropertyChanged(nameof(PreviewImage));
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private static BitmapImage? LoadImageFromDisk(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource         = new Uri(path, UriKind.Absolute);
            bmp.CacheOption       = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth  = 200;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public SpeciesCardViewModel(SpeciesSummary summary)
    {
        Summary      = summary;
        CaptureCount = summary.CaptureCount;
        LatestCaptureAt = summary.LatestCaptureAt;

        // Prefer iNaturalist reference photo; fall back to latest capture image
        PreviewImagePath = !string.IsNullOrEmpty(summary.ReferencePhotoPath)
            ? summary.ReferencePhotoPath
            : summary.LatestImagePath ?? string.Empty;

        FirstSeenLabel = $"First seen: {summary.FirstDetectedAt:d MMM yyyy}";

        var daysDiff = (DateTime.Now.Date - LatestCaptureAt.Date).TotalDays;
        LastSeenLabel = daysDiff == 0 ? "Last seen: today"
                      : daysDiff == 1 ? "Last seen: yesterday"
                      : $"Last seen: {LatestCaptureAt:d MMM}";
    }
}
