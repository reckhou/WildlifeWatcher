# POI Burst Mode & Pipeline Improvements

**Goal:** Replace single-frame POI detection with a multi-frame burst that accumulates a heatmap of the busiest regions, while also refactoring the background model to run standalone, making the grid resolution dynamic with user-friendly presets, and fixing three existing bugs.

**Architecture:** When a normal tick's single-frame POI extraction finds motion, a burst capture runs (N frames at configurable interval). Each burst frame's hot-cell grid is accumulated into a shared heatmap. After the burst, the heatmap is thresholded and BFS extracts the busiest regions. Crops are taken from the frame where each region was strongest. The background model is decoupled to its own timer so it stays fresh independently of detection frequency. The grid resolution adapts to camera resolution via a preset cell-size selector.

**Tech Stack:** C# / .NET / WPF / CommunityToolkit.Mvvm / SkiaSharp / LibVLCSharp

---

## Progress

- [ ] Task 1: EMA standalone refactor + dynamic resolution
- [ ] Task 2: Dynamic grid resolution with presets
- [ ] Task 3: Burst mode with heatmap accumulation
- [ ] Task 4: Bug fixes (VLC OSD, recent captures, source_crop_index)
- [ ] Task 5: Settings UI for burst mode and grid presets

---

## Files

- Modify: `Services/BackgroundModelService.cs` — split ProcessFrame into UpdateBackground + ComputeForeground, own timer, dynamic resolution
- Modify: `Services/Interfaces/IBackgroundModelService.cs` — new API surface
- Modify: `Services/PointOfInterestService.cs` — dynamic grid dimensions, heatmap accumulation mode
- Modify: `Services/Interfaces/IPointOfInterestService.cs` — new burst/heatmap methods
- Modify: `Services/RecognitionLoopService.cs` — burst capture loop, standalone EMA timer, debug burst
- Modify: `Services/RtspCameraService.cs` — disable VLC OSD
- Modify: `Services/CaptureStorageService.cs` — source_crop_index fallback + validation
- Modify: `Models/AppConfiguration.cs` — new settings
- Modify: `Models/DetectionEvent.cs` — add ThumbnailJpeg property
- Modify: `ViewModels/DetectionSettingsViewModel.cs` — burst + grid preset UI bindings
- Modify: `Views/Pages/LiveViewPage.xaml` — recent captures bind to POI crop

All paths relative to `src/WildlifeWatcher/WildlifeWatcher/`.

---

### Task 1: EMA Standalone Refactor + Dynamic Resolution

**Files:** `Services/BackgroundModelService.cs`, `Services/Interfaces/IBackgroundModelService.cs`, `Services/RecognitionLoopService.cs`, `Models/AppConfiguration.cs`

The background model currently runs at the detection tick interval (default 30s). Refactor it to run on its own timer and support dynamic resolution derived from camera frame size.

**1a. New settings in `AppConfiguration.cs`:**

```csharp
/// <summary>
/// How often the background model captures a frame and updates the EMA (seconds).
/// Independent of the detection interval. Lower = better background quality, faster training.
/// Range: 1–30. Default: 2.
/// </summary>
public int BackgroundUpdateIntervalSeconds { get; set; } = 2;
```

**1b. Split `ProcessFrame` in `BackgroundModelService`:**

Replace the single `ProcessFrame(byte[] pngFrame)` with:

```csharp
/// <summary>
/// Update the EMA background model only. Called by the standalone timer.
/// Adapts the background toward the current frame. Does NOT produce foreground/temporal outputs.
/// </summary>
void UpdateBackground(byte[] pngFrame);

/// <summary>
/// Compute foreground and temporal delta for a given frame against the current background.
/// Read-only — does not mutate the background model. Returns new arrays each call.
/// The caller provides previousGray for temporal delta (null on first call = zero delta).
/// </summary>
(float[] foreground, float[] temporalDelta) ComputeForeground(byte[] pngFrame, byte[]? previousGray);
```

Implementation in `BackgroundModelService`:

- `UpdateBackground`: does the EMA blend (`background[i] = α·gray[i] + (1-α)·background[i]`), increments `_frameCount`, fires `TrainingProgressChanged`. Stores `_previousGray` for its own internal use. Uses a `lock(_lock)` around background array writes.
- `ComputeForeground`: takes `lock(_lock)` to snapshot the current `_background` array, then computes `foreground[i] = |gray[i] - background[i]|` and `temporalDelta[i] = |gray[i] - previousGray[i]|` into **new** arrays (no shared state mutation). The caller owns `previousGray` tracking — this keeps burst frames' temporal deltas relative to each other, not to the EMA's last frame.
- Keep `ProcessFrame` as a convenience that calls both (for backward compat during transition), but mark it `[Obsolete]`.

**1c. Dynamic resolution:**

Replace hardcoded `W=160, H=120` with properties derived from the first frame seen:

```csharp
private int _width;
private int _height;

public int Width  => _width;
public int Height => _height;

/// <summary>
/// Initialize the model resolution. Called once when the first frame arrives
/// or when camera resolution changes. Resets the model.
/// </summary>
public void Initialize(int downscaleWidth, int downscaleHeight);
```

`Initialize` sets `_width`/`_height`, allocates arrays, resets frame count. Called from `RecognitionLoopService` when it first learns the camera resolution (or when grid preset changes). The downscale dimensions are computed by the POI service based on cell size preset and camera resolution (see Task 2).

**1d. Standalone timer in `RecognitionLoopService`:**

Add a second loop method `RunBackgroundUpdateLoopAsync` that:
- Runs on its own `Task.Run` from `StartAsync`
- Every `BackgroundUpdateIntervalSeconds`, extracts a frame and calls `_background.UpdateBackground(frame)`
- Independent of `ProcessTickAsync` — runs even during cooldown, burst, etc.

Remove the `_background.ProcessFrame(currentFrame)` call from `ProcessTickAsync`. Instead, `ProcessTickAsync` calls `_background.ComputeForeground(currentFrame, previousGray)` to get foreground data for that specific frame.

Similarly update `TriggerTestPoiAsync` to use `ComputeForeground` instead of `ProcessFrame`.

**1e. File format migration:**

Increment `FileVersion` to 4. Add `_width` and `_height` to the persisted state. `LoadState` rejects files with mismatched dimensions (returns false → cold start).

**Verify:** App starts, background model trains at 2s intervals (check logs: "Background training X%" messages appear every ~2s instead of every 30s). Training completes in ~2 minutes instead of ~30 minutes. Detection still works — POI regions appear on the live view overlay.

---

### Task 2: Dynamic Grid Resolution with Presets

**Files:** `Services/PointOfInterestService.cs`, `Services/Interfaces/IPointOfInterestService.cs`, `Models/AppConfiguration.cs`

**Depends on:** Task 1 (dynamic resolution in BackgroundModelService)

Replace hardcoded 32x24 grid with dynamic dimensions derived from camera resolution and a configurable cell size.

**2a. New settings in `AppConfiguration.cs`:**

```csharp
/// <summary>
/// Target cell size in real camera pixels. Controls POI grid granularity.
/// Smaller = finer grid = detects smaller animals, but more cells to process.
/// Presets: Ultra Fine (20), Very Fine (30), Fine (40), Standard (50),
/// Moderate (60), Coarse (70), Very Coarse (80). Default: Fine (40).
/// </summary>
public int PoiCellSizePixels { get; set; } = 40;
```

**2b. Dynamic grid computation in `PointOfInterestService`:**

Remove the `const` grid dimensions. Add a method to compute grid dimensions from camera resolution:

```csharp
/// <summary>
/// Compute grid dimensions and downscale resolution for a given camera resolution and cell size.
/// </summary>
public static (int gridCols, int gridRows, int downscaleW, int downscaleH)
    ComputeGridDimensions(int cameraWidth, int cameraHeight, int cellSizePixels)
{
    int gridCols = Math.Max(4, cameraWidth / cellSizePixels);
    int gridRows = Math.Max(3, cameraHeight / cellSizePixels);
    const int cellPixels = 5; // pixels per cell in downscaled frame
    return (gridCols, gridRows, gridCols * cellPixels, gridRows * cellPixels);
}
```

For a 2K camera (2560x1440) at Fine (40px): grid = 64x36, downscale = 320x180.

Update `ExtractRegions` to accept `gridCols` and `gridRows` as parameters (or store them as instance state set via an `Initialize` method). All internal logic that references `GridCols`/`GridRows`/`ScaleWidth`/`ScaleHeight` uses instance fields instead of constants.

**2c. Initialization flow:**

When `RecognitionLoopService` first extracts a frame, it:
1. Decodes the frame to get camera resolution (width × height)
2. Calls `PointOfInterestService.ComputeGridDimensions(cameraW, cameraH, settings.PoiCellSizePixels)`
3. Calls `_poi.Initialize(gridCols, gridRows)` to set instance state
4. Calls `_background.Initialize(downscaleW, downscaleH)` to match
5. Caches camera resolution; reinitializes if it changes or if `PoiCellSizePixels` setting changes

**2d. Preset labels (for UI — consumed in Task 5):**

Define a static helper for UI display:

```csharp
public static class PoiCellSizePresets
{
    public static readonly (int Size, string Name, string Description)[] All =
    {
        (20, "Ultra Fine",  "Tiny subjects, distant animals, insects"),
        (30, "Very Fine",   "Small birds (wrens, tits), mice"),
        (40, "Fine",        "Medium birds (robins, starlings)"),
        (50, "Standard",    "Squirrels, rabbits, pigeons"),
        (60, "Moderate",    "Cats, magpies, larger birds"),
        (70, "Coarse",      "Foxes, herons, large subjects"),
        (80, "Very Coarse", "Deer, very large/close animals"),
    };
}
```

**Verify:** Change the preset in settings. Check logs for grid dimension messages (e.g., "POI grid initialized: 64×36 (cell size 40px for 2560×1440 camera)"). Test POI one-shot at different presets — finer presets should detect smaller regions.

---

### Task 3: Burst Mode with Heatmap Accumulation

**Files:** `Services/RecognitionLoopService.cs`, `Services/PointOfInterestService.cs`, `Services/Interfaces/IPointOfInterestService.cs`, `Services/Interfaces/IRecognitionLoopService.cs`, `Models/AppConfiguration.cs`

**Depends on:** Task 1, Task 2

**3a. New settings in `AppConfiguration.cs`:**

```csharp
/// <summary>Enable multi-frame burst capture after initial POI detection.</summary>
public bool EnableBurstCapture { get; set; } = true;

/// <summary>Number of frames to capture during a burst. Range: 3–30. Default: 10.</summary>
public int BurstFrameCount { get; set; } = 10;

/// <summary>Milliseconds between burst frame captures. Range: 200–3000. Default: 1000.</summary>
public int BurstIntervalMs { get; set; } = 1000;
```

**3b. Heatmap methods in `PointOfInterestService`:**

```csharp
/// <summary>
/// Compute the hot-cell grid for a single frame (same logic as step 1 of ExtractRegions).
/// Returns a 2D array of cell intensities (0 = cold, positive = hot with intensity weight).
/// </summary>
public float[,] ComputeHotCellGrid(float[] foreground, float[]? temporalDelta,
    IReadOnlyList<MotionZone>? whitelistZones,
    int pixelThreshold, double poiSensitivity,
    int temporalThreshold, double temporalCellFraction);

/// <summary>
/// Accumulate a hot-cell grid into a running heatmap (element-wise addition).
/// </summary>
public void AccumulateHeatmap(float[,] heatmap, float[,] hotCells);

/// <summary>
/// Extract POI regions from an accumulated heatmap. Thresholds cells,
/// runs BFS connected components, and returns bounding boxes (normalized).
/// Does NOT produce crops — the caller provides the best frame per region for cropping.
/// </summary>
public IReadOnlyList<(int minRow, int maxRow, int minCol, int maxCol, float peakIntensity)>
    ExtractHeatmapRegions(float[,] heatmap, int minHitCount, double poiSensitivity);

/// <summary>
/// Crop a region from a full-resolution frame, given grid-space bounding box.
/// Reuses existing padding/clamping/encoding logic.
/// </summary>
public PoiRegion? CropRegion(byte[] frame, int minRow, int maxRow, int minCol, int maxCol, int index);
```

For `ComputeHotCellGrid`: instead of binary hot/cold, compute the **average foreground intensity** per cell when the cell passes both thresholds. This gives the heatmap a weighted signal — cells with strong motion contribute more. Return 0 for cells that don't pass thresholds.

For `ExtractHeatmapRegions`: threshold = `minHitCount` (default 2, meaning a cell must be hot in at least 2 frames). BFS and size filtering reuse existing logic. Return grid-space bounding boxes with peak intensity per region (used to find the best frame).

**3c. Burst capture in `RecognitionLoopService.ProcessTickAsync`:**

After the existing single-frame POI extraction finds ≥1 region and `EnableBurstCapture` is true:

```
1. Allocate heatmap[gridRows, gridCols] = all zeros
2. Track: byte[]? burstPreviousGray = null  (for temporal delta within burst)
3. Track: (byte[] frame, float[,] hotCells)[] burstFrames  (to find best frame per region)
4. For i in 0..BurstFrameCount-1:
   a. await Task.Delay(BurstIntervalMs)
   b. Extract frame from camera
   c. (fg, td) = _background.ComputeForeground(frame, burstPreviousGray)
   d. burstPreviousGray = current gray pixels (need to expose from ComputeForeground or extract separately)
   e. hotCells = _poi.ComputeHotCellGrid(fg, td, zones, ...)
   f. _poi.AccumulateHeatmap(heatmap, hotCells)
   g. Store (frame, hotCells) in burstFrames
5. regions = _poi.ExtractHeatmapRegions(heatmap, minHitCount: 2, poiSensitivity)
6. For each region:
   a. Find the burst frame where this region's cells had the highest total intensity
   b. Crop from that frame using _poi.CropRegion(bestFrame, ...)
7. Proceed to cooldown check and AI call with burst-derived poiRegions
```

If burst is disabled or the initial POI finds 0 regions, behavior is unchanged (existing single-frame path).

**3d. Update `ComputeForeground` to also return gray pixels:**

The burst loop needs `previousGray` for temporal delta within the burst sequence. Extend the return:

```csharp
(float[] foreground, float[] temporalDelta, byte[] grayPixels) ComputeForeground(byte[] pngFrame, byte[]? previousGray);
```

The caller passes the previous call's `grayPixels` as `previousGray` for the next burst frame.

**3e. Debug flow updates:**

`TriggerTestPoiAsync`: When burst is enabled, run one full burst (all N frames) instead of a single-frame POI test. Return status like `"Burst POI: 3 region(s) from 10 frames, saved to captures folder."` Save debug images for heatmap regions if `SavePoiDebugImages` is true.

`ToggleContinuousTestAsync` in `DetectionSettingsViewModel`: Clamp `ContinuousTestIntervalSeconds` floor to `ceil(BurstFrameCount * BurstIntervalMs / 1000) + 2`. If the current value is below the floor when burst is enabled, auto-adjust upward and log.

**3f. Update `PoiRegionsDetected` event:**

Fire `PoiRegionsDetected` with the burst-derived regions (not the initial single-frame regions) so the live overlay shows the final merged regions.

**Verify:** Enable burst mode, point camera at a scene with movement. Check logs for burst messages: "Burst capture: frame 1/10", ..., "Burst complete: 3 heatmap regions extracted". Verify the POI overlay shows burst-derived regions. One-shot debug test triggers a full burst. Auto-trigger interval is floored to burst duration + 2s.

---

### Task 4: Bug Fixes

**Files:** `Services/RtspCameraService.cs`, `Views/Pages/LiveViewPage.xaml`, `Models/DetectionEvent.cs`, `Services/RecognitionLoopService.cs`, `Services/CaptureStorageService.cs`

Three independent fixes grouped together.

**4a. Remove VLC snapshot OSD overlay:**

In `RtspCameraService` constructor, add `"--no-osd"` to LibVLC initialization:

```csharp
_libVlc = new LibVLC("--verbose=1", "--no-osd");
```

This suppresses VLC's built-in on-screen display when `TakeSnapshot()` is called.

**4b. Recent captures show POI crop instead of full frame:**

Add a computed property to `DetectionEvent` that returns the matched POI crop bytes:

```csharp
// In DetectionEvent.cs
/// <summary>
/// JPEG bytes of the POI crop that triggered this detection.
/// Falls back to the full frame PNG if no POI match.
/// </summary>
public byte[] ThumbnailBytes
{
    get
    {
        if (Result.SourcePoiIndex.HasValue && PoiRegions.Count > 0)
        {
            var match = PoiRegions.FirstOrDefault(p => p.Index == Result.SourcePoiIndex.Value);
            if (match != null) return match.CroppedJpeg;
        }
        // Fallback: if POI regions exist but no index match, use first region
        if (PoiRegions.Count > 0)
            return PoiRegions[0].CroppedJpeg;
        return FramePng;
    }
}
```

Update `LiveViewPage.xaml` line 283-285:

```xml
<Image Height="100" Stretch="UniformToFill"
       Source="{Binding ThumbnailBytes, Converter={StaticResource BytesToBitmapConverter}}"
       Margin="0,0,0,6"/>
```

**4c. Fix gallery when AI omits `source_crop_index`:**

In `CaptureStorageService.SaveCaptureAsync`, after the existing `SourcePoiIndex` lookup block (line 152-163), add a fallback:

```csharp
// Store source POI bounding box for zoomed thumbnail display
if (result.SourcePoiIndex.HasValue && poiRegions is { Count: > 0 })
{
    var srcPoi = poiRegions.FirstOrDefault(p => p.Index == result.SourcePoiIndex.Value);
    if (srcPoi != null)
    {
        record.PoiNLeft   = srcPoi.NLeft;
        record.PoiNTop    = srcPoi.NTop;
        record.PoiNWidth  = srcPoi.NWidth;
        record.PoiNHeight = srcPoi.NHeight;
    }
    else
    {
        _logger.LogWarning(
            "source_crop_index {Index} did not match any POI region (count={Count})",
            result.SourcePoiIndex.Value, poiRegions.Count);
    }
}
else if (!result.SourcePoiIndex.HasValue && poiRegions is { Count: > 0 })
{
    // AI omitted source_crop_index despite receiving crops — fall back to first region
    _logger.LogWarning(
        "AI omitted source_crop_index but {Count} POI region(s) were sent — falling back to region 1",
        poiRegions.Count);
    var fallback = poiRegions[0];
    record.PoiNLeft   = fallback.NLeft;
    record.PoiNTop    = fallback.NTop;
    record.PoiNWidth  = fallback.NWidth;
    record.PoiNHeight = fallback.NHeight;
}
```

**Verify:**
- 4a: Connect to camera, trigger a snapshot — no flash/text overlay on the video.
- 4b: Trigger a detection — the "Recent Detections" panel shows a zoomed-in crop, not the full frame.
- 4c: Check logs after AI calls — if `source_crop_index` is missing, see the warning and confirm the gallery still shows a cropped thumbnail (not full frame).

---

### Task 5: Settings UI for Burst Mode and Grid Presets

**Files:** `ViewModels/DetectionSettingsViewModel.cs`, `Views/Pages/DetectionSettingsPage.xaml` (or whichever XAML hosts detection settings)

**Depends on:** Task 1, Task 2, Task 3

**5a. New ViewModel properties:**

```csharp
// ── Burst Mode ────────────────────────────────────────────────────────
[ObservableProperty] private bool _enableBurstCapture = true;
[ObservableProperty] private int  _burstFrameCount = 10;
[ObservableProperty] private int  _burstIntervalMs = 1000;
[ObservableProperty] private int  _backgroundUpdateIntervalSeconds = 2;

// ── Grid Resolution ──────────────────────────────────────────────────
[ObservableProperty] private int _poiCellSizePixels = 40;
```

Add `AutoSave` hooks for all new properties. Add advice strings:

```csharp
public string BurstAdvice =>
    EnableBurstCapture
        ? $"Burst: {BurstFrameCount} frames over {BurstFrameCount * BurstIntervalMs / 1000.0:F1}s. " +
          $"Auto-test floor: {BurstFrameCount * BurstIntervalMs / 1000 + 2}s."
        : "Burst disabled — single-frame POI only.";

public string GridPresetAdvice
{
    get
    {
        var preset = PoiCellSizePresets.All.FirstOrDefault(p => p.Size == PoiCellSizePixels);
        var name = preset.Name ?? $"Custom ({PoiCellSizePixels}px)";
        var desc = preset.Description ?? "";
        return $"{name} — {desc}";
    }
}
```

**5b. Load/save in `LoadSettings` and `AutoSave`:**

```csharp
// LoadSettings:
EnableBurstCapture              = s.EnableBurstCapture;
BurstFrameCount                 = s.BurstFrameCount;
BurstIntervalMs                 = s.BurstIntervalMs;
BackgroundUpdateIntervalSeconds = s.BackgroundUpdateIntervalSeconds;
PoiCellSizePixels               = s.PoiCellSizePixels;

// AutoSave:
s.EnableBurstCapture              = EnableBurstCapture;
s.BurstFrameCount                 = BurstFrameCount;
s.BurstIntervalMs                 = BurstIntervalMs;
s.BackgroundUpdateIntervalSeconds = BackgroundUpdateIntervalSeconds;
s.PoiCellSizePixels               = PoiCellSizePixels;
```

**5c. Clamp continuous test interval when burst is enabled:**

In `OnEnableBurstCaptureChanged`, `OnBurstFrameCountChanged`, `OnBurstIntervalMsChanged`:

```csharp
private void ClampContinuousTestInterval()
{
    if (!EnableBurstCapture) return;
    int burstDurationSec = (int)Math.Ceiling(BurstFrameCount * BurstIntervalMs / 1000.0);
    int floor = burstDurationSec + 2;
    if (ContinuousTestIntervalSeconds < floor)
        ContinuousTestIntervalSeconds = floor;
}
```

**5d. XAML layout:**

Add a "Grid Resolution" section with a `Slider` (Minimum=20, Maximum=80, TickFrequency=10, IsSnapToTickEnabled=True) bound to `PoiCellSizePixels`. Show `GridPresetAdvice` below it.

Add a "Burst Capture" section with:
- `CheckBox` for `EnableBurstCapture`
- `Slider` or `TextBox` for `BurstFrameCount` (3–30)
- `Slider` or `TextBox` for `BurstIntervalMs` (200–3000, step 100)
- `BurstAdvice` text below
- `Slider` or `TextBox` for `BackgroundUpdateIntervalSeconds` (1–30)

Place these in the existing POI section of the settings page, between the existing POI sensitivity controls and the test buttons.

**Verify:** Open settings page. Slide the grid resolution preset slider — see the name and description update. Toggle burst capture on/off — see the advice text update. Set burst to 10 frames × 1000ms, then check continuous test interval cannot go below 12s.

---
