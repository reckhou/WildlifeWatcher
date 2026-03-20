using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IMotionDetectionService
{
    bool HasMotion(float[] foreground, double sensitivity, int pixelThreshold,
                   IReadOnlyList<MotionZone>? whitelistZones = null);
}
