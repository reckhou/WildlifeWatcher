using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Models;

public class DetectionEvent
{
    public DateTime DetectedAt { get; init; } = DateTime.Now;
    public RecognitionResult Result { get; init; } = null!;
    public byte[] FramePng { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<PoiRegion> PoiRegions { get; init; } = Array.Empty<PoiRegion>();
}
