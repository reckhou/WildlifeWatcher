namespace WildlifeWatcher.Services.Interfaces;

public interface IBackgroundModelService
{
    /// <summary>
    /// Initialize the model resolution. Called once when the first frame arrives
    /// or when grid preset changes. Resets the model.
    /// </summary>
    void Initialize(int downscaleWidth, int downscaleHeight);

    /// <summary>
    /// Update the EMA background model only. Called by the standalone timer.
    /// Adapts the background toward the current frame. Does NOT produce foreground/temporal outputs.
    /// </summary>
    void UpdateBackground(byte[] pngFrame);

    /// <summary>
    /// Compute foreground and temporal delta for a given frame against the current background.
    /// Read-only — does not mutate the background model.
    /// The caller provides previousGray for temporal delta (null on first call = zero delta).
    /// Returns new arrays each call. grayPixels can be passed as previousGray on the next call.
    /// </summary>
    (float[] foreground, float[] temporalDelta, byte[] grayPixels) ComputeForeground(byte[] pngFrame, byte[]? previousGray);

    /// <summary>Convenience method — calls UpdateBackground then ComputeForeground internally.</summary>
    [Obsolete("Use UpdateBackground and ComputeForeground separately.")]
    void ProcessFrame(byte[] pngFrame);

    /// <summary>Per-pixel foreground intensity (0–255). Null until first frame processed via ProcessFrame.</summary>
    float[]? Foreground { get; }

    /// <summary>Per-pixel temporal delta (0–255). Null until second frame processed via ProcessFrame.</summary>
    float[]? TemporalDelta { get; }

    int Width  { get; }
    int Height { get; }

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

    /// <summary>UTC time when the model was last saved or loaded from disk. Null if cold-started.</summary>
    DateTime? SavedAt { get; }

    /// <summary>Frames needed until the initial frame contributes &lt;5% to the EMA background.</summary>
    int TrainingFramesNeeded { get; }

    /// <summary>Training completion ratio clamped to [0.0, 1.0].</summary>
    double TrainingProgress { get; }

    /// <summary>True once FrameCount >= TrainingFramesNeeded.</summary>
    bool IsTrainingComplete { get; }

    /// <summary>Fired on every UpdateBackground call with the current progress (0.0–1.0).</summary>
    event EventHandler<double> TrainingProgressChanged;

    /// <summary>
    /// Immediately completes training by advancing the frame count to the required threshold
    /// and firing TrainingProgressChanged(1.0).
    /// </summary>
    void SkipTraining();
}
