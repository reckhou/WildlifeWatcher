namespace WildlifeWatcher.Services.Interfaces;

public interface IBackgroundModelService
{
    /// <summary>Decode frame, update EMA background, compute foreground mask.</summary>
    void ProcessFrame(byte[] pngFrame);

    /// <summary>Per-pixel foreground intensity (0–255). Null until first frame processed.</summary>
    float[]? Foreground { get; }

    int Width  { get; }  // 160
    int Height { get; } // 120

    void Reset();

    /// <summary>Persist the current background model to disk so it survives app restarts.</summary>
    void SaveState();

    /// <summary>
    /// Restore a previously saved background model from disk.
    /// Returns true if a compatible state file was found and loaded; false if cold-starting.
    /// </summary>
    bool LoadState();

    /// <summary>Delete any persisted state file so the next connect always cold-starts.</summary>
    void DeleteSavedState();

    /// <summary>Number of frames processed since last Reset or Load.</summary>
    int FrameCount { get; }

    /// <summary>Frames needed until the initial frame contributes &lt;5% to the EMA background.</summary>
    int TrainingFramesNeeded { get; }

    /// <summary>Training completion ratio clamped to [0.0, 1.0].</summary>
    double TrainingProgress { get; }

    /// <summary>True once FrameCount >= TrainingFramesNeeded.</summary>
    bool IsTrainingComplete { get; }

    /// <summary>Fired on every ProcessFrame call with the current progress (0.0–1.0).</summary>
    event EventHandler<double> TrainingProgressChanged;

    /// <summary>
    /// Immediately completes training by advancing the frame count to the required threshold
    /// and firing TrainingProgressChanged(1.0).
    /// </summary>
    void SkipTraining();
}
