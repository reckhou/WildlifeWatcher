using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IPointOfInterestService
{
    /// <summary>
    /// Set the grid dimensions. Must be called before ExtractRegions.
    /// </summary>
    void Initialize(int gridCols, int gridRows);

    /// <summary>
    /// Uses the foreground mask from the background model to find hotspot regions,
    /// then returns padded JPEG crops from the full-resolution current frame.
    /// Returns an empty list when motion is too diffuse to isolate distinct hotspots.
    /// When <paramref name="whitelistZones"/> is provided, only hot cells inside those
    /// zones contribute to regions; all others are ignored.
    /// </summary>
    IReadOnlyList<PoiRegion> ExtractRegions(float[] foreground, byte[] currentFrame,
                                             IReadOnlyList<MotionZone>? whitelistZones = null,
                                             int pixelThreshold = 25,
                                             double poiSensitivity = 0.5,
                                             float[]? temporalDelta = null,
                                             int temporalThreshold = 8,
                                             double temporalCellFraction = 0.10);
}
