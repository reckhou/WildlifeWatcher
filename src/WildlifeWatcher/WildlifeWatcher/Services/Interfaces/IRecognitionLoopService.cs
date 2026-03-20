using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IRecognitionLoopService
{
    bool IsRunning   { get; }
    bool IsAnalyzing { get; }
    bool IsDebugMode { get; set; }
    event EventHandler<DetectionEvent>               DetectionOccurred;
    event EventHandler<bool>                         IsAnalyzingChanged;
    /// <summary>Fired every tick when POI regions are extracted (empty list clears the overlay).</summary>
    event EventHandler<IReadOnlyList<PoiRegion>>?    PoiRegionsDetected;
}
