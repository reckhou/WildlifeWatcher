using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ICaptureStorageService
{
    event EventHandler<CaptureRecord> CaptureSaved;
    Task SaveCaptureAsync(byte[] framePng, RecognitionResult result, IReadOnlyList<PoiRegion>? poiRegions = null);
    Task<IReadOnlyList<Species>> GetAllSpeciesWithCapturesAsync();
    Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId);
    Task DeleteCaptureAsync(int captureId);
    Task UpdateCaptureNotesAsync(int captureId, string notes);
    Task ResetGalleryAsync(string capturesDirectory);
}
