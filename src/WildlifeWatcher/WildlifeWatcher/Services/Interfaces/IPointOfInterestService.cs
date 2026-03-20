using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IPointOfInterestService
{
    /// <summary>
    /// Uses the foreground mask from the background model to find hotspot regions,
    /// then returns padded JPEG crops from the full-resolution current frame.
    /// Returns an empty list when motion is too diffuse to isolate distinct hotspots.
    /// </summary>
    IReadOnlyList<PoiRegion> ExtractRegions(float[] foreground, byte[] currentFrame);
}
