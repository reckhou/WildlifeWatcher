using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class MotionDetectionService : IMotionDetectionService
{
    private const int FgWidth  = 160;
    private const int FgHeight = 120;

    public bool HasMotion(float[] foreground, double sensitivity, int pixelThreshold,
                          IReadOnlyList<MotionZone>? whitelistZones = null)
    {
        bool filterZones = whitelistZones is { Count: > 0 };

        bool InZone(int x, int y) =>
            whitelistZones!.Any(z =>
                x / (double)FgWidth  >= z.NLeft && x / (double)FgWidth  <= z.NLeft + z.NWidth &&
                y / (double)FgHeight >= z.NTop  && y / (double)FgHeight <= z.NTop  + z.NHeight);

        int changed = 0, total = 0;
        for (int i = 0; i < foreground.Length; i++)
        {
            int px = i % FgWidth;
            int py = i / FgWidth;
            if (filterZones && !InZone(px, py)) continue;
            total++;
            if (foreground[i] > pixelThreshold) changed++;
        }

        if (total == 0) return false;
        double fraction        = (double)changed / total;
        // sensitivity=1.0 → triggers on any change; sensitivity=0.0 → needs 8% changed
        double triggerFraction = (1.0 - sensitivity) * 0.08;
        return fraction >= triggerFraction;
    }
}
