namespace WildlifeWatcher.Services.Interfaces;

public record SpeciesCandidate(
    string CommonName,
    string ScientificName,
    double Confidence,
    string Description);

public record RecognitionResult(
    bool Detected,
    string CommonName,
    string ScientificName,
    double Confidence,
    string Description,
    string RawResponse,
    IReadOnlyList<SpeciesCandidate> Candidates,
    int? SourcePoiIndex);           // 1-based; null when full frame was used

public interface IAiRecognitionService
{
    /// <summary>
    /// Recognizes wildlife. When <paramref name="poiJpegs"/> is non-empty the crops
    /// are sent to the AI instead of the full frame, improving detection of small subjects.
    /// </summary>
    Task<RecognitionResult> RecognizeAsync(
        byte[] fullFramePng,
        IReadOnlyList<byte[]>? poiJpegs = null,
        CancellationToken ct = default);
}
