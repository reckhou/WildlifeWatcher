using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ICaptureStorageService
{
    event EventHandler<CaptureRecord> CaptureSaved;
    Task SaveCaptureAsync(byte[] framePng, RecognitionResult result, IReadOnlyList<PoiRegion>? poiRegions = null, DateTime? batchStartedAt = null);
    Task<IReadOnlyList<Species>> GetAllSpeciesWithCapturesAsync();
    Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId);
    Task DeleteCaptureAsync(int captureId);
    Task UpdateCaptureNotesAsync(int captureId, string notes);
    Task ResetGalleryAsync(string capturesDirectory);
    Task UpdateSpeciesReferencePhotoAsync(int speciesId, string localPath);
    Task MergeSpeciesByScientificNameAsync();

    // Calendar queries (Phase 6)
    Task<Dictionary<DateTime, int>> GetCaptureDateCountsForMonthAsync(int year, int month);
    Task<IReadOnlyList<CaptureRecord>> GetCapturesByDateAsync(DateTime date);
}
