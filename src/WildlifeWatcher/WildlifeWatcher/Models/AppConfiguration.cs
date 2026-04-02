using System.IO;

namespace WildlifeWatcher.Models;

public enum AiProvider { Claude, Gemini }

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
    /// Describes the camera's environment, inserted into the AI system prompt.
    /// E.g. "a woodland edge with a pond". Must not be blank — default is "a garden".
    /// </summary>
    public string AiHabitatDescription { get; set; } = "a garden";

    /// <summary>
    /// Optional species focus hint appended to the AI system prompt.
    /// E.g. "UK wildlife, particularly birds and small mammals".
    /// Omitted from the prompt when blank.
    /// </summary>
    public string AiTargetSpeciesHint { get; set; } = "Wildlife, particularly birds";

    /// <summary>
    /// How often a frame is grabbed from the camera and analysed for motion.
    /// Runs unconditionally — the loop wakes up every N seconds regardless of cooldown or detection state.
    /// </summary>
    public int FrameExtractionIntervalSeconds { get; set; } = 30;
    public double MinConfidenceThreshold { get; set; } = 0.7;
    public AiProvider AiProvider { get; set; } = AiProvider.Claude;
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

    /// <summary>
    /// Minimum per-pixel temporal delta (0–255) to count as "motion" (frame-to-frame change).
    /// Required in addition to foreground difference to mark a cell as hot.
    /// Range: 3–30. Default 8 (filters static shadows while detecting actual motion).
    /// </summary>
    public int MotionTemporalThreshold { get; set; } = 8;

    /// <summary>
    /// Fraction of changed pixels (via temporal delta) required in a cell to mark it as temporally hot.
    /// Required in addition to foreground hot fraction. Lower = more sensitive to motion.
    /// Range: 0.03–0.25. Default 0.10.
    /// </summary>
    public double MotionTemporalCellFraction { get; set; } = 0.10;

    public List<MotionZone> MotionWhitelistZones { get; set; } = new();

    /// <summary>
    /// How often the background model captures a frame and updates the EMA (seconds).
    /// Independent of the detection interval. Lower = better background quality, faster training.
    /// Range: 1–30. Default: 2.
    /// </summary>
    public int BackgroundUpdateIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Target cell size in real camera pixels. Controls POI grid granularity.
    /// Smaller = finer grid = detects smaller animals, but more cells to process.
    /// Presets: Ultra Fine (20), Very Fine (30), Fine (40), Standard (50),
    /// Moderate (60), Coarse (70), Very Coarse (80). Default: Fine (40).
    /// </summary>
    public int PoiCellSizePixels { get; set; } = 40;

    /// <summary>Maximum number of POI regions sent to the AI per frame. Range: 3–10. Default: 5.</summary>
    public int MaxPoiRegions { get; set; } = 5;

    /// <summary>Enable multi-frame burst capture after initial POI detection.</summary>
    public bool EnableBurstCapture { get; set; } = true;

    /// <summary>Number of frames to capture during a burst. Range: 3–30. Default: 10.</summary>
    public int BurstFrameCount { get; set; } = 10;

    /// <summary>Milliseconds between burst frame captures. Range: 200–3000. Default: 1000.</summary>
    public int BurstIntervalMs { get; set; } = 1000;

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

    /// <summary>
    /// When true, AI detection only runs within the daylight window
    /// [sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes].
    /// Background model training continues unconditionally.
    /// </summary>
    public bool EnableDaylightDetectionOnly { get; set; } = false;

    /// <summary>
    /// Minutes offset applied to sunrise. Negative = before sunrise, positive = after.
    /// Default: -30 (start detecting 30 minutes before sunrise).
    /// </summary>
    public int SunriseOffsetMinutes { get; set; } = -30;

    /// <summary>
    /// Minutes offset applied to sunset. Positive = after sunset, negative = before.
    /// Default: 30 (stop detecting 30 minutes after sunset).
    /// </summary>
    public int SunsetOffsetMinutes { get; set; } = 30;

    /// <summary>
    /// UI scale multiplier applied independently of the Windows display scale setting.
    /// Range: 0.8–2.0. Default: 1.0.
    /// </summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>
    /// Interval in seconds between automatic POI test cycles when continuous testing is active.
    /// Range: 1–30. Default: 5.
    /// </summary>
    public int PoiTestIntervalSeconds { get; set; } = 5;

    public string GetEffectiveDatabasePath() =>
        string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "WildlifeWatcher", "wildlife.db")
            : DatabasePath;
}
