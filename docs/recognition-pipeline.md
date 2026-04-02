# Recognition Pipeline

## Overview

`RecognitionLoopService` runs as a background hosted service with two independent loops:

1. **Background update loop** — runs every `BackgroundUpdateIntervalSeconds` (default 2s), updating the EMA background model independently of detection.
2. **Detection loop** — runs every `FrameExtractionIntervalSeconds` (configurable), calling `ProcessTickAsync`.

---

## Step 0 — Dynamic initialization

On the first frame (or when camera resolution/cell size changes), `EnsureInitialized` runs:

1. Decodes the frame to get camera resolution (width × height)
2. Calls `PointOfInterestService.ComputeGridDimensions(cameraW, cameraH, PoiCellSizePixels)` → `(gridCols, gridRows, downscaleW, downscaleH)`
3. Calls `_poi.Initialize(gridCols, gridRows)` and `_background.Initialize(downscaleW, downscaleH)`
4. Tries to restore persisted background model from disk (rejects mismatched dimensions)

Grid resolution adapts to the camera via `PoiCellSizePixels` (default 40px). Presets: Ultra Fine (20px) through Very Coarse (80px). Example: 2K camera (2560×1440) at Fine (40px) → grid 64×36, downscale 320×180.

---

## Step 1 — Extract a frame

`_camera.ExtractFrameAsync()` grabs the current frame from the RTSP stream as a PNG byte array. If the camera isn't connected, the tick is skipped entirely.

---

## Step 2 — Compute foreground (read-only)

`_background.ComputeForeground(frame, previousGray)` produces foreground and temporal delta arrays **without mutating** the background model. The background model is updated separately by the standalone background update loop.

```
foreground[i] = |gray[i] - background[i]|   (snapshot under lock)
temporalDelta[i] = |gray[i] - previousGray[i]|
```

**Background update loop** (standalone, every `BackgroundUpdateIntervalSeconds`):
```
background[i] = α * gray[i] + (1-α) * background[i]
```

`α` (`MotionBackgroundAlpha`) controls how fast the background adapts. The standalone loop means the background trains at ~2s intervals even when detection runs at 30s intervals — training completes in ~2 minutes instead of ~30.

**Training gate**: the first N frames are consumed purely to stabilise the background model. Until `IsTrainingComplete`, the detection loop returns early. The model is persisted to `%APPDATA%\WildlifeWatcher\background_model.bin` on exit and reloaded on startup — if the saved model is less than 2 hours old and dimensions match, it skips the training wait entirely.

---

## Step 3 — POI extraction (`PointOfInterestService`)

If `EnablePoiExtraction` is on, this turns the foreground mask into a set of cropped JPEG regions to send to AI instead of the full frame:

1. **Build a hot-cell grid** — each cell covers a 5×5 pixel block in the downscaled foreground. Grid dimensions are dynamic (set by `ComputeGridDimensions`). A cell is "hot" only if **both** conditions are met:
   - **Foreground condition**: enough pixels exceed `MotionPixelThreshold` (significant difference from EMA background)
   - **Temporal condition**: enough pixels exceed `MotionTemporalThreshold` (significant frame-to-frame change, indicating actual motion)

   This dual requirement eliminates static false positives (shadows, reflections, camera noise) while preserving detection of low-contrast moving subjects.

2. **Mask out cells outside whitelist zones** (if configured).

3. **BFS flood-fill** to group connected hot cells into blobs. Uses 8-neighbor connectivity at sensitivity ≥ 0.3, otherwise 4-neighbor. Small blobs below `minCellCount` are discarded.

4. **Sort blobs by size, take the top 5.**

5. **Map each blob back to the full-resolution frame**: scales the grid coordinates back up, adds 40% padding around the tight bounding box, clamps the padded crop to 25% of the frame in each dimension (to avoid oversized crops), and caps at 640px on the longest side.

6. **Encode each crop as JPEG at 85% quality.**

If 0 regions are found: tick is skipped.

---

## Step 3b — Burst capture (optional)

If `EnableBurstCapture` is on and the initial single-frame POI found ≥1 region, a multi-frame burst runs:

1. Capture `BurstFrameCount` frames at `BurstIntervalMs` intervals (wall-clock scheduled — extraction and processing time is absorbed into the interval, not added on top)
2. For each frame, compute foreground via `ComputeForeground` and build a **weighted hot-cell grid** (cells store average foreground intensity, not just binary)
3. Accumulate each grid into a shared **heatmap** (element-wise addition)
4. After the burst, threshold the heatmap (cells must be hot in ≥2 frames) and BFS-extract regions
5. For each heatmap region, find the burst frame where that region's cells had peak total intensity
6. Crop from the best frame per region

The burst replaces the initial single-frame POI regions with heatmap-derived regions. This filters out transient noise (hot in 1 frame only) and selects the sharpest frame for each detected animal.

---

## Step 4 — Cooldown check

If a detection already happened recently (`DateTime.UtcNow < _cooldownUntil`), the AI call is skipped. The cooldown is set when a detection is saved and lasts `CooldownSeconds`.

---

## Step 5 — AI recognition (`ClaudeRecognitionService` / `GeminiRecognitionService`)

`_ai.RecognizeAsync(currentFrame, poiJpegs, ct)` is called.

**If POI crops exist** → sends each crop as a separate labelled image ("Region 1:", image, "Region 2:", image…) with a prompt asking to identify animals per region.

**If no POI crops** → falls back to the full frame, resizing it to max 1280px and compressing to JPEG 85% first.

The system prompt is assembled dynamically by `PromptBuilder` from three user-configured fields in `DetectionSettingsWindow`:
- `AiHabitatDescription` (default: "a garden") — describes the camera environment
- `LocationName` — injected as "Location: ..." when set; omitted when blank
- `AiTargetSpeciesHint` (default: "Wildlife, particularly birds") — injected as "Focus on: ..." when set; omitted when blank

The system prompt instructs the model to respond with strict JSON:

```json
{
  "detections": [
    {
      "source_crop_index": 1,
      "detected": true,
      "candidates": [
        { "common_name": "...", "scientific_name": "...", "confidence": 0.92, "description": "..." }
      ]
    }
  ]
}
```

Up to 3 candidate species per detection, ordered by confidence.

---

## Step 6 — Filter results and save

For each `detected: true` result:

- Skip if `confidence < MinConfidenceThreshold`
- Set the cooldown (`_cooldownUntil = now + CooldownSeconds`)
- Call `_captureStorage.SaveCaptureAsync(frame, result, poiRegions, ...)` to persist the capture
- If AI omits `source_crop_index` but POI regions were sent, fall back to region 1 (with warning log)
- Fire `DetectionOccurred` event (used by the UI to show a notification)

---

## Step 2.5 — Daylight window gate (`SunriseSunsetService`)

If `EnableDaylightDetectionOnly` is `true`, this gate blocks AI detection outside the configured window. Background model updates and training always continue regardless.

**Detection window:** `[sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes]`

- Sunrise/sunset fetched from Open-Meteo once per calendar day (fire-and-forget, cached)
- No location configured → 06:00–20:00 local fallback; `IsUsingFallback = true`
- Weather fetch fails → reuse yesterday's cache; if no cache, use 06:00–20:00 fallback
- Sign convention: negative offset = before the base time, positive = after

When blocked, fires `DaylightWindowChanged(false)` on the first blocked tick. Fires `DaylightWindowChanged(true)` when detection resumes.

---

## Summary

```
background update loop (every BackgroundUpdateIntervalSeconds)
  → extract frame
  → ensure initialized (dynamic grid + downscale dims)
  → _background.UpdateBackground(frame)

detection loop (every FrameExtractionIntervalSeconds)
  → extract frame
  → ensure initialized
  → compute foreground (read-only snapshot of background)
  → [gate: background seeded?]
  → [gate: training complete?]
  → trigger daily sunrise/sunset refresh (fire-and-forget)
  → [gate: daylight window?]           ← SunriseSunsetService
  → extract POI crops             ← PointOfInterestService BFS on hot-cell grid
  → [gate: POI count > 0?]
  → [optional: burst capture]     ← heatmap accumulation over N frames
  → [gate: cooldown expired?]
  → send to AI (POI crops or full frame)
  → filter by confidence
  → save capture + fire event
```
