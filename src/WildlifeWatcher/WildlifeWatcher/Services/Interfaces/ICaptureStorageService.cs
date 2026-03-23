using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

/// <summary>Lightweight projection of a species for gallery display — avoids loading all captures.</summary>
public record SpeciesSummary(
    int       SpeciesId,
    string    CommonName,
    string    ScientificName,
    string?   Description,
    DateTime  FirstDetectedAt,
    string?   ReferencePhotoPath,
    int       CaptureCount,
    DateTime  LatestCaptureAt,
    string?   LatestImagePath);

public interface ICaptureStorageService
{
    event EventHandler<CaptureRecord> CaptureSaved;
    Task SaveCaptureAsync(byte[] framePng, RecognitionResult result, IReadOnlyList<PoiRegion>? poiRegions = null, DateTime? batchStartedAt = null);
    Task<IReadOnlyList<SpeciesSummary>> GetAllSpeciesSummariesAsync();
    Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId);
    Task DeleteCaptureAsync(int captureId);
    Task UpdateCaptureNotesAsync(int captureId, string notes);
    Task ReassignCaptureAsync(int captureId, int newSpeciesId);
    Task<IReadOnlyList<SpeciesSummary>> GetAllSpeciesAsync();
    Task ResetGalleryAsync(string capturesDirectory);
    Task UpdateSpeciesReferencePhotoAsync(int speciesId, string localPath);
    Task MergeSpeciesByScientificNameAsync();

    // Calendar queries (Phase 6)
    Task<Dictionary<DateTime, DailySummary>> GetCaptureDailySummaryForMonthAsync(int year, int month);
    Task<IReadOnlyList<CaptureRecord>> GetCapturesByDateAsync(DateTime date);
}

public record DailySummary(int Count, string? WeatherCondition);
