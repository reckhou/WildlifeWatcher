namespace WildlifeWatcher.Services.Interfaces;

public record RecognitionResult(
    bool Detected,
    string CommonName,
    string ScientificName,
    double Confidence,
    string Description,
    string RawResponse);

public interface IAiRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(byte[] jpegFrame, CancellationToken ct = default);
}
