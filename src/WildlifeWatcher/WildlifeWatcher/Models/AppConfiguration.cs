using System.IO;

namespace WildlifeWatcher.Models;

public enum AiProvider { Claude, Gemini, LocalOnly }

public class AppConfiguration
{
    public string RtspUrl { get; set; } = string.Empty;
    /// <summary>
    /// Suppresses AI calls for this many seconds after a species is saved.
    /// The camera still runs and POIs are still detected during cooldown — only the API call is skipped.
    /// Prevents repeated alerts for the same animal still in frame.
    /// </summary>
    public int CooldownSeconds { get; set; } = 30;
    public string CapturesDirectory { get; set; } = "captures";
    public string ClaudeModel { get; set; } = "claude-haiku-4-5-20251001";
    public string GeminiModel { get; set; } = "gemini-2.0-flash";
    /// <summary>
    /// How often a frame is grabbed from the camera and analysed for motion.
    /// Runs unconditionally — the loop wakes up every N seconds regardless of cooldown or detection state.
    /// </summary>
    public int FrameExtractionIntervalSeconds { get; set; } = 30;
    public double MinConfidenceThreshold { get; set; } = 0.7;
    public AiProvider AiProvider { get; set; } = AiProvider.Claude;
    public bool EnableLocalPreFilter { get; set; } = true;
    public string LocalModelPath { get; set; } = string.Empty;
    public double MotionSensitivity { get; set; } = 0.5;
    public bool EnablePoiExtraction { get; set; } = true;
    public bool SavePoiDebugImages { get; set; } = true;

    /// <summary>EMA update rate for background model. Lower = background adapts more slowly = better small-subject retention. Range: 0.01–0.20.</summary>
    public double MotionBackgroundAlpha { get; set; } = 0.05;

    /// <summary>
    /// Minimum per-pixel foreground intensity (0–255) to count as "changed".
    /// Lower values detect small, low-contrast subjects like birds on pavement.
    /// Range: 5–50. Default 25 (clears sensor noise and JPEG compression artefacts).
    /// </summary>
    public int MotionPixelThreshold { get; set; } = 25;

    public List<MotionZone> MotionWhitelistZones { get; set; } = new();

    public string DatabasePath { get; set; } = string.Empty;

    public string GetEffectiveDatabasePath() =>
        string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "WildlifeWatcher", "wildlife.db")
            : DatabasePath;
}
