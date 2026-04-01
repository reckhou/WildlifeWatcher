using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Models;

public class DetectionEvent
{
    public DateTime DetectedAt { get; init; } = DateTime.Now;
    public RecognitionResult Result { get; init; } = null!;
    public byte[] FramePng { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<PoiRegion> PoiRegions { get; init; } = Array.Empty<PoiRegion>();
    /// <summary>The DB record written by this detection. Null only if the save was skipped.</summary>
    public CaptureRecord? SavedRecord { get; init; }

    /// <summary>
    /// JPEG bytes of the POI crop that triggered this detection.
    /// Falls back to the full frame PNG if no POI match.
    /// </summary>
    public byte[] ThumbnailBytes
    {
        get
        {
            if (Result.SourcePoiIndex.HasValue && PoiRegions.Count > 0)
            {
                var match = PoiRegions.FirstOrDefault(p => p.Index == Result.SourcePoiIndex.Value);
                if (match != null) return match.CroppedJpeg;
            }
            // Fallback: if POI regions exist but no index match, use first region
            if (PoiRegions.Count > 0)
                return PoiRegions[0].CroppedJpeg;
            return FramePng;
        }
    }
}
