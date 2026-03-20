using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class MotionDetectionService : IMotionDetectionService
{
    public bool HasMotion(float[] foreground, double sensitivity, int pixelThreshold)
    {
        int changed = 0;
        for (int i = 0; i < foreground.Length; i++)
        {
            if (foreground[i] > pixelThreshold) changed++;
        }

        double fraction        = (double)changed / foreground.Length;
        // sensitivity=1.0 → triggers on any change; sensitivity=0.0 → needs 8% changed
        double triggerFraction = (1.0 - sensitivity) * 0.08;
        return fraction >= triggerFraction;
    }
}
