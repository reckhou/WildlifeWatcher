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

    /// <summary>
    /// Per-species save cooldown in minutes. If the same species was already saved within this window,
    /// the new capture is discarded (file is not written, no DB record is created).
    /// Set to 0 to disable. Default: 5 minutes.
    /// </summary>
    public int SpeciesCooldownMinutes { get; set; } = 5;
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
    public string LocalModelPath { get; set; } = string.Empty;
    public bool EnablePoiExtraction { get; set; } = true;
    public bool SavePoiDebugImages { get; set; } = true;

    /// <summary>
    /// Controls how aggressively POI extraction isolates small regions.
    /// 0.0 = conservative (large subjects only), 1.0 = aggressive (small/distant subjects).
    /// </summary>
    public double PoiSensitivity { get; set; } = 0.5;

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

    // Location for weather data (Phase 6)
    public double? Latitude     { get; set; }
    public double? Longitude    { get; set; }
    public string  LocationName { get; set; } = string.Empty;

    /// <summary>Camera audio volume (0–200). Persisted across sessions.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>
    /// Debug option: forces the update check to return a fake available update without hitting GitHub.
    /// Set to true temporarily to test the update banner at runtime. Default: false.
    /// </summary>
    public bool DebugForceUpdateAvailable { get; set; } = false;

    public string GetEffectiveDatabasePath() =>
        string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "WildlifeWatcher", "wildlife.db")
            : DatabasePath;
}
