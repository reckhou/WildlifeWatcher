using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IRecognitionLoopService
{
    bool IsRunning   { get; }
    bool IsAnalyzing { get; }
    event EventHandler<DetectionEvent>               DetectionOccurred;
    event EventHandler<bool>                         IsAnalyzingChanged;
    /// <summary>Fired every tick when POI regions are extracted (empty list clears the overlay).</summary>
    event EventHandler<IReadOnlyList<PoiRegion>>?    PoiRegionsDetected;
    /// <summary>
    /// Fired when detection is paused or resumed due to the daylight window gate.
    /// True = detection allowed (window opened). False = detection paused (window closed).
    /// Only fires on state transitions, not every tick.
    /// </summary>
    event EventHandler<bool> DaylightWindowChanged;

    /// <summary>
    /// Fired during burst capture with (framesCompleted, totalFrames).
    /// framesCompleted == 0 signals burst start; framesCompleted == totalFrames signals burst end.
    /// </summary>
    event EventHandler<(int completed, int total)>? BurstProgressChanged;

    /// <summary>
    /// Runs a single POI detection tick (frame extract → background model → POI extraction → save debug crops).
    /// Skips AI recognition. Returns a summary string describing what was found and saved.
    /// </summary>
    Task<string> TriggerTestPoiAsync();
}
