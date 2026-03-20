using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Data;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class CaptureStorageService : ICaptureStorageService
{
    private readonly IDbContextFactory<WildlifeDbContext> _dbFactory;
    private readonly ISettingsService    _settings;
    private readonly IWeatherService     _weather;
    private readonly ILogger<CaptureStorageService> _logger;

    public event EventHandler<CaptureRecord>? CaptureSaved;

    public CaptureStorageService(
        IDbContextFactory<WildlifeDbContext> dbFactory,
        ISettingsService settings,
        IWeatherService  weather,
        ILogger<CaptureStorageService> logger)
    {
        _dbFactory = dbFactory;
        _settings  = settings;
        _weather   = weather;
        _logger    = logger;
    }

    public async Task SaveCaptureAsync(byte[] framePng, RecognitionResult result, IReadOnlyList<PoiRegion>? poiRegions = null, DateTime? batchStartedAt = null)
    {
        // Per-species cooldown: skip saving if this species was captured before this batch started.
        // Captures made within the same batch (batchStartedAt) are excluded from the check so that
        // multiple individuals of the same species returned by one AI query are all recorded.
        var cooldownMinutes = _settings.CurrentSettings.SpeciesCooldownMinutes;
        if (cooldownMinutes > 0)
        {
            var batchCutoff = batchStartedAt ?? DateTime.Now;
            await using var checkDb = await _dbFactory.CreateDbContextAsync();
            var latestCapture = await checkDb.CaptureRecords
                .Where(c =>
                    c.CapturedAt < batchCutoff &&
                    (c.Species.CommonName.ToLower() == result.CommonName.ToLower() ||
                     (!string.IsNullOrEmpty(result.ScientificName) &&
                      c.Species.ScientificName.ToLower() == result.ScientificName.ToLower())))
                .OrderByDescending(c => c.CapturedAt)
                .Select(c => (DateTime?)c.CapturedAt)
                .FirstOrDefaultAsync();

            if (latestCapture.HasValue &&
                batchCutoff - latestCapture.Value < TimeSpan.FromMinutes(cooldownMinutes))
            {
                _logger.LogInformation(
                    "Species cooldown: {Species} was saved {Ago:F1} min ago (cooldown {Limit} min) — skipping",
                    result.CommonName,
                    (batchCutoff - latestCapture.Value).TotalMinutes,
                    cooldownMinutes);
                return;
            }
        }

        var captureDir = ResolveCapturesDir(_settings.CurrentSettings.CapturesDirectory);
        var safeName   = string.Concat(result.CommonName.Split(Path.GetInvalidFileNameChars()))
                               .Replace(' ', '_');
        var baseName   = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var fileName   = $"{baseName}.jpg";
        var filePath   = Path.Combine(captureDir, fileName);

        var jpegBytes = ConvertPngToJpeg(framePng);
        await File.WriteAllBytesAsync(filePath, jpegBytes);
        _logger.LogInformation("Capture saved: {Path}", filePath);

        string? annotatedPath = null;
        if (poiRegions is { Count: > 0 })
        {
            var speciesLabel   = $"{result.CommonName} ({result.Confidence:P0})";
            var annotatedBytes = await DrawPoiOverlay(jpegBytes, poiRegions, speciesLabel, result.SourcePoiIndex);
            annotatedPath      = Path.Combine(captureDir, $"{baseName}_annotated.jpg");
            await File.WriteAllBytesAsync(annotatedPath, annotatedBytes);
            _logger.LogInformation("Annotated capture saved: {Path}", annotatedPath);

            if (_settings.CurrentSettings.SavePoiDebugImages)
            {
                foreach (var poi in poiRegions)
                {
                    bool isSource = result.SourcePoiIndex == null || poi.Index == result.SourcePoiIndex;
                    var cropLabel = isSource
                        ? speciesLabel
                        : $"POI {poi.Index} — Sent to AI (not matched)";
                    var cropBytes = await LabelCropJpeg(poi.CroppedJpeg, cropLabel);
                    var cropPath  = Path.Combine(captureDir, $"{baseName}_poi_{poi.Index}.jpg");
                    await File.WriteAllBytesAsync(cropPath, cropBytes);
                }
                _logger.LogInformation("Saved {Count} labeled POI crop(s) for {Base}", poiRegions.Count, baseName);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync();

        var species = await db.Species
            .FirstOrDefaultAsync(s => s.CommonName.ToLower() == result.CommonName.ToLower());

        if (species == null && !string.IsNullOrEmpty(result.ScientificName))
            species = await db.Species
                .FirstOrDefaultAsync(s => s.ScientificName.ToLower() == result.ScientificName.ToLower());

        if (species == null)
        {
            species = new Species
            {
                CommonName      = result.CommonName,
                ScientificName  = result.ScientificName,
                Description     = result.Description,
                FirstDetectedAt = DateTime.Now
            };
            db.Species.Add(species);
            await db.SaveChangesAsync();
        }

        // Fetch weather if location is configured
        var cfg = _settings.CurrentSettings;
        WeatherSnapshot? weather = null;
        if (cfg.Latitude.HasValue && cfg.Longitude.HasValue)
        {
            weather = await _weather.GetCurrentWeatherAsync(cfg.Latitude.Value, cfg.Longitude.Value);
        }

        var record = new CaptureRecord
        {
            SpeciesId              = species.Id,
            CapturedAt             = DateTime.Now,
            ImageFilePath          = filePath,
            AiRawResponse          = result.RawResponse,
            ConfidenceScore        = result.Confidence,
            AnnotatedImageFilePath = annotatedPath,
            AlternativesJson       = result.Candidates.Count > 1
                ? JsonSerializer.Serialize(result.Candidates.Skip(1))
                : null,
            Temperature      = weather?.Temperature,
            WeatherCondition = weather?.Condition,
            WindSpeed        = weather?.WindSpeed,
            Precipitation    = weather?.Precipitation,
            Sunrise          = weather?.Sunrise,
            Sunset           = weather?.Sunset,
        };
        db.CaptureRecords.Add(record);
        await db.SaveChangesAsync();

        record.Species = species;
        CaptureSaved?.Invoke(this, record);
    }

    public async Task ResetGalleryAsync(string capturesDirectory)
    {
        var dir = ResolveCapturesDir(capturesDirectory);
        foreach (var f in Directory.GetFiles(dir, "*.jpg")
                           .Concat(Directory.GetFiles(dir, "*.png")))
            File.Delete(f);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.CaptureRecords.ExecuteDeleteAsync();
        await db.Species.ExecuteDeleteAsync();
    }

    public async Task<IReadOnlyList<Species>> GetAllSpeciesWithCapturesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Species
            .Include(s => s.Captures)
            .Where(s => s.Captures.Any())
            .ToListAsync();
    }

    public async Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CaptureRecords
            .Include(c => c.Species)
            .Where(r => r.SpeciesId == speciesId)
            .OrderByDescending(r => r.CapturedAt)
            .ToListAsync();
    }

    public async Task DeleteCaptureAsync(int captureId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.CaptureRecords.FindAsync(captureId);
        if (record == null) return;
        if (File.Exists(record.ImageFilePath)) File.Delete(record.ImageFilePath);
        if (record.AnnotatedImageFilePath is { Length: > 0 } ap && File.Exists(ap)) File.Delete(ap);

        // Delete any POI crops that share the same base name
        var dir      = Path.GetDirectoryName(record.ImageFilePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(record.ImageFilePath);
        foreach (var crop in Directory.GetFiles(dir, $"{baseName}_poi_*.jpg"))
            File.Delete(crop);

        db.CaptureRecords.Remove(record);
        await db.SaveChangesAsync();
    }

    public async Task UpdateCaptureNotesAsync(int captureId, string notes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.CaptureRecords.FindAsync(captureId);
        if (record == null) return;
        record.Notes = notes;
        await db.SaveChangesAsync();
    }

    public async Task UpdateSpeciesReferencePhotoAsync(int speciesId, string localPath)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var species = await db.Species.FindAsync(speciesId);
        if (species == null) return;
        species.ReferencePhotoPath = localPath;
        await db.SaveChangesAsync();
    }

    public async Task MergeSpeciesByScientificNameAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var allSpecies = await db.Species.ToListAsync();

        var groups = allSpecies
            .Where(s => !string.IsNullOrWhiteSpace(s.ScientificName))
            .GroupBy(s => s.ScientificName.ToLower())
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var canonical   = group.OrderBy(s => s.FirstDetectedAt).First();
            var duplicates  = group.Where(s => s.Id != canonical.Id).ToList();
            var duplicateIds = duplicates.Select(s => s.Id).ToList();

            await db.CaptureRecords
                .Where(c => duplicateIds.Contains(c.SpeciesId))
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.SpeciesId, canonical.Id));

            var earliestDate = group.Min(s => s.FirstDetectedAt);
            if (canonical.FirstDetectedAt > earliestDate)
            {
                canonical.FirstDetectedAt = earliestDate;
                db.Species.Update(canonical);
            }

            db.Species.RemoveRange(duplicates);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Merged {Count} duplicate(s) of '{Name}' (ScientificName: {Sci}) into species id={Id}",
                duplicates.Count, canonical.CommonName, canonical.ScientificName, canonical.Id);
        }
    }

    public async Task<Dictionary<DateTime, int>> GetCaptureDateCountsForMonthAsync(int year, int month)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var start = new DateTime(year, month, 1);
        var end   = start.AddMonths(1);

        var dates = await db.CaptureRecords
            .Where(c => c.CapturedAt >= start && c.CapturedAt < end)
            .Select(c => c.CapturedAt.Date)
            .ToListAsync();

        return dates.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<IReadOnlyList<CaptureRecord>> GetCapturesByDateAsync(DateTime date)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var start = date.Date;
        var end   = start.AddDays(1);

        return await db.CaptureRecords
            .Include(c => c.Species)
            .Where(c => c.CapturedAt >= start && c.CapturedAt < end)
            .OrderBy(c => c.CapturedAt)
            .ToListAsync();
    }

    private static async Task<byte[]> DrawPoiOverlay(byte[] jpegBytes, IReadOnlyList<PoiRegion> regions, string speciesLabel, int? sourcePoiIndex)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            using var ms = new MemoryStream(jpegBytes);
            var source   = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            double w     = source.PixelWidth;
            double h     = source.PixelHeight;

            var pen = new System.Windows.Media.Pen(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136)), 3);
            pen.Freeze();
            var textBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136));
            textBrush.Freeze();
            var bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 0, 0, 0));
            bgBrush.Freeze();
            var typeface  = new System.Windows.Media.Typeface("Arial");
            double fontSize = Math.Max(14, w * 0.016);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new System.Windows.Rect(0, 0, w, h));

                foreach (var poi in regions)
                {
                    var rect = new System.Windows.Rect(
                        poi.NLeft * w, poi.NTop * h, poi.NWidth * w, poi.NHeight * h);
                    dc.DrawRectangle(null, pen, rect);

                    bool isSource = sourcePoiIndex == null || poi.Index == sourcePoiIndex;
                    if (isSource)
                    {
                        var ft = new System.Windows.Media.FormattedText(
                            speciesLabel,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Windows.FlowDirection.LeftToRight,
                            typeface, fontSize, textBrush, 1.0);

                        // Place label just above the box; clamp to top edge
                        double labelY = Math.Max(0, rect.Y - ft.Height - 4);
                        var labelBg   = new System.Windows.Rect(rect.X, labelY, ft.Width + 8, ft.Height + 4);
                        dc.DrawRectangle(bgBrush, null, labelBg);
                        dc.DrawText(ft, new System.Windows.Point(rect.X + 4, labelY + 2));
                    }
                }
            }

            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        });
    }

    internal static async Task<byte[]> LabelCropJpeg(byte[] jpegCrop, string label)
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            using var ms = new MemoryStream(jpegCrop);
            var source   = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            double w     = source.PixelWidth;
            double h     = source.PixelHeight;

            var textBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136));
            textBrush.Freeze();
            var bgBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0));
            bgBrush.Freeze();
            var typeface  = new System.Windows.Media.Typeface("Arial");
            double fontSize = Math.Max(12, w * 0.045);

            var ft = new System.Windows.Media.FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface, fontSize, textBrush, 1.0);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(source, new System.Windows.Rect(0, 0, w, h));
                // Label strip at the bottom of the crop
                var bgRect = new System.Windows.Rect(0, h - ft.Height - 8, w, ft.Height + 8);
                dc.DrawRectangle(bgBrush, null, bgRect);
                dc.DrawText(ft, new System.Windows.Point(4, h - ft.Height - 4));
            }

            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var outMs = new MemoryStream();
            encoder.Save(outMs);
            return outMs.ToArray();
        });
    }

    private static byte[] ConvertPngToJpeg(byte[] pngBytes, int quality = 85)
    {
        using var ms  = new MemoryStream(pngBytes);
        var frame     = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        var encoder   = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var outMs = new MemoryStream();
        encoder.Save(outMs);
        return outMs.ToArray();
    }

    private static string ResolveCapturesDir(string configured)
    {
        var dir = configured;
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeWatcher", dir);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
