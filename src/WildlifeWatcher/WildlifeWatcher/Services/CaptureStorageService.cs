using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;
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
                    (EF.Functions.Collate(c.Species.CommonName, "NOCASE") == result.CommonName ||
                     (!string.IsNullOrEmpty(result.ScientificName) &&
                      EF.Functions.Collate(c.Species.ScientificName, "NOCASE") == result.ScientificName)))
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

        var now        = DateTime.Now;
        var captureDir = ResolveCapturesDir(_settings.CurrentSettings.CapturesDirectory);
        var safeName   = string.Concat(result.CommonName.Split(Path.GetInvalidFileNameChars()))
                               .Replace(' ', '_');
        var baseName   = $"{safeName}_{now:yyyyMMdd_HHmmss}";
        var fileName   = $"{baseName}.jpg";
        var filePath   = Path.Combine(captureDir, fileName);

        var jpegBytes = ConvertPngToJpeg(framePng, quality: 100);
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
            .FirstOrDefaultAsync(s => EF.Functions.Collate(s.CommonName, "NOCASE") == result.CommonName);

        if (species == null && !string.IsNullOrEmpty(result.ScientificName))
            species = await db.Species
                .FirstOrDefaultAsync(s => EF.Functions.Collate(s.ScientificName, "NOCASE") == result.ScientificName);

        if (species == null)
        {
            species = new Species
            {
                CommonName      = result.CommonName,
                ScientificName  = result.ScientificName,
                Description     = result.Description,
                FirstDetectedAt = now
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
            CapturedAt             = now,
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

        // Store source POI bounding box for zoomed thumbnail display
        if (result.SourcePoiIndex.HasValue && poiRegions != null)
        {
            var srcPoi = poiRegions.FirstOrDefault(p => p.Index == result.SourcePoiIndex.Value);
            if (srcPoi != null)
            {
                record.PoiNLeft   = srcPoi.NLeft;
                record.PoiNTop    = srcPoi.NTop;
                record.PoiNWidth  = srcPoi.NWidth;
                record.PoiNHeight = srcPoi.NHeight;
            }
        }
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

    public async Task<IReadOnlyList<SpeciesSummary>> GetAllSpeciesSummariesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Species
            .Where(s => s.Captures.Any())
            .Select(s => new SpeciesSummary(
                s.Id,
                s.CommonName,
                s.ScientificName,
                s.Description,
                s.FirstDetectedAt,
                s.ReferencePhotoPath,
                s.Captures.Count,
                s.Captures.Max(c => c.CapturedAt),
                s.Captures.OrderByDescending(c => c.CapturedAt).Select(c => c.ImageFilePath).FirstOrDefault()))
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

    public async Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId, int skip, int take)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CaptureRecords
            .Include(c => c.Species)
            .Where(r => r.SpeciesId == speciesId)
            .OrderByDescending(r => r.CapturedAt)
            .Skip(skip)
            .Take(take)
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

            _logger.LogInformation(
                "Merged {Count} duplicate(s) of '{Name}' (ScientificName: {Sci}) into species id={Id}",
                duplicates.Count, canonical.CommonName, canonical.ScientificName, canonical.Id);
        }

        if (groups.Count > 0)
            await db.SaveChangesAsync();
    }

    public async Task<Dictionary<DateTime, DailySummary>> GetCaptureDailySummaryForMonthAsync(int year, int month)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var start = new DateTime(year, month, 1);
        var end   = start.AddMonths(1);

        var rows = await db.CaptureRecords
            .Where(c => c.CapturedAt >= start && c.CapturedAt < end)
            .Select(c => new {
                c.CapturedAt,
                c.WeatherCondition,
                c.Temperature,
                c.Precipitation,
                c.Sunrise,
                c.Sunset
            })
            .ToListAsync();

        return rows
            .GroupBy(c => c.CapturedAt.Date)
            .ToDictionary(
                g => g.Key,
                g => new DailySummary(
                    g.Count(),
                    g.Select(c => c.WeatherCondition).FirstOrDefault(w => w != null),
                    g.Select(c => c.Temperature).FirstOrDefault(v => v.HasValue),
                    g.Select(c => c.Precipitation).FirstOrDefault(v => v.HasValue),
                    g.Select(c => c.Sunrise).FirstOrDefault(v => v.HasValue),
                    g.Select(c => c.Sunset).FirstOrDefault(v => v.HasValue)));
    }

    public async Task ReassignCaptureAsync(int captureId, int newSpeciesId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var record = await db.CaptureRecords.FindAsync(captureId);
        if (record == null) return;

        var oldSpeciesId = record.SpeciesId;
        record.SpeciesId = newSpeciesId;
        await db.SaveChangesAsync();

        // Clean up orphaned species (one that has no captures left)
        var oldSpeciesHasCaptures = await db.CaptureRecords.AnyAsync(c => c.SpeciesId == oldSpeciesId);
        if (!oldSpeciesHasCaptures)
        {
            var orphan = await db.Species.FindAsync(oldSpeciesId);
            if (orphan != null)
            {
                db.Species.Remove(orphan);
                await db.SaveChangesAsync();
            }
        }
    }

    public async Task<IReadOnlyList<SpeciesSummary>> GetAllSpeciesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Species
            .OrderBy(s => s.CommonName)
            .Select(s => new SpeciesSummary(
                s.Id,
                s.CommonName,
                s.ScientificName,
                s.Description,
                s.FirstDetectedAt,
                s.ReferencePhotoPath,
                s.Captures.Count,
                s.Captures.Any() ? s.Captures.Max(c => c.CapturedAt) : s.FirstDetectedAt,
                s.Captures.OrderByDescending(c => c.CapturedAt).Select(c => c.ImageFilePath).FirstOrDefault()))
            .ToListAsync();
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

    public async Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAndDateAsync(int speciesId, DateTime date)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var start = date.Date;
        var end   = start.AddDays(1);
        return await db.CaptureRecords
            .Include(c => c.Species)
            .Where(c => c.SpeciesId == speciesId && c.CapturedAt >= start && c.CapturedAt < end)
            .OrderByDescending(c => c.CapturedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<DateTime>> GetCaptureDatesForSpeciesAsync(int speciesId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var dates = await db.CaptureRecords
            .Where(c => c.SpeciesId == speciesId)
            .Select(c => c.CapturedAt.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToListAsync();
        return dates;
    }

    private static Task<byte[]> DrawPoiOverlay(byte[] jpegBytes, IReadOnlyList<PoiRegion> regions, string speciesLabel, int? sourcePoiIndex)
    {
        return Task.Run(() =>
        {
            using var bitmap = SKBitmap.Decode(jpegBytes);
            using var canvas = new SKCanvas(bitmap);
            float w = bitmap.Width;
            float h = bitmap.Height;

            using var boxPaint = new SKPaint
            {
                Color       = new SKColor(0, 255, 136),
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                IsAntialias = true
            };
            using var textPaint = new SKPaint { Color = new SKColor(0, 255, 136), IsAntialias = true };
            using var font      = new SKFont(SKTypeface.FromFamilyName("Arial"), Math.Max(14, w * 0.016f));
            using var bgPaint   = new SKPaint { Color = new SKColor(0, 0, 0, 180), Style = SKPaintStyle.Fill };

            foreach (var poi in regions)
            {
                var rect = SKRect.Create(
                    (float)(poi.NLeft * w), (float)(poi.NTop * h),
                    (float)(poi.NWidth * w), (float)(poi.NHeight * h));
                canvas.DrawRect(rect, boxPaint);

                bool isSource = sourcePoiIndex == null || poi.Index == sourcePoiIndex;
                if (isSource)
                {
                    float textWidth  = font.MeasureText(speciesLabel);
                    float textHeight = font.Size;
                    float labelY     = Math.Max(0, rect.Top - textHeight - 4);

                    canvas.DrawRect(rect.Left, labelY, textWidth + 8, textHeight + 4, bgPaint);
                    canvas.DrawText(speciesLabel, rect.Left + 4, labelY + textHeight, SKTextAlign.Left, font, textPaint);
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        });
    }

    internal static Task<byte[]> LabelCropJpeg(byte[] jpegCrop, string label)
    {
        return Task.Run(() =>
        {
            using var bitmap = SKBitmap.Decode(jpegCrop);
            using var canvas = new SKCanvas(bitmap);
            float w = bitmap.Width;
            float h = bitmap.Height;

            float fontSize = Math.Max(12, w * 0.045f);
            using var textPaint = new SKPaint { Color = new SKColor(0, 255, 136), IsAntialias = true };
            using var font      = new SKFont(SKTypeface.FromFamilyName("Arial"), fontSize);
            using var bgPaint   = new SKPaint { Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Fill };

            // Label strip at the bottom of the crop
            canvas.DrawRect(0, h - fontSize - 8, w, fontSize + 8, bgPaint);
            canvas.DrawText(label, 4, h - 4, SKTextAlign.Left, font, textPaint);

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        });
    }

    private static byte[] ConvertPngToJpeg(byte[] pngBytes, int quality = 85)
    {
        using var bitmap = SKBitmap.Decode(pngBytes);
        using var image  = SKImage.FromBitmap(bitmap);
        using var data   = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    internal static string ResolveCapturesDir(string configured)
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
