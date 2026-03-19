using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ICaptureStorageService
{
    Task<CaptureRecord> SaveCaptureAsync(byte[] jpegFrame, RecognitionResult result);
    Task<IEnumerable<Species>> GetAllSpeciesWithCapturesAsync();
    Task<IEnumerable<CaptureRecord>> GetCapturesBySpeciesAsync(int speciesId);
    Task DeleteCaptureAsync(int captureId);
    Task UpdateCaptureNotesAsync(int captureId, string notes);
}
