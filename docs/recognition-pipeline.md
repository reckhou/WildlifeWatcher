# Recognition Pipeline

## Overview

`RecognitionLoopService` runs as a background hosted service. Every `FrameExtractionIntervalSeconds` (configurable) it calls `ProcessTickAsync`.

---

## Step 1 — Extract a frame

`_camera.ExtractFrameAsync()` grabs the current frame from the RTSP stream as a PNG byte array. If the camera isn't connected, the tick is skipped entirely.

---

## Step 2 — Update the background model (`BackgroundModelService`)

Every frame gets fed into an **EMA (Exponential Moving Average)** background model running at 160×120 grayscale:

```
foreground[i] = |gray[i] - background[i]|
background[i] = α * gray[i] + (1-α) * background[i]
```

`α` (`MotionBackgroundAlpha`) controls how fast the background adapts. The foreground array is a per-pixel "how different is this pixel from what we expect" score.

**Training gate**: the first N frames are consumed purely to stabilise the background model. Until `IsTrainingComplete`, the loop returns early. The model is persisted to `%APPDATA%\WildlifeWatcher\background_model.bin` on exit and reloaded on startup — if the saved model is less than 2 hours old it skips the training wait entirely.

---

## Step 3 — Motion pre-filter (`MotionDetectionService`)

If `EnableLocalPreFilter` is on, `HasMotion()` checks the foreground array:

- Counts pixels where `foreground[i] > MotionPixelThreshold` (changed enough to matter)
- If whitelist zones are configured, only pixels inside those zones are counted
- Calculates `fraction = changed / total`
- Compares against `triggerFraction = (1.0 - sensitivity) * 0.08`
  - sensitivity=1.0 → triggers on any change
  - sensitivity=0.0 → needs 8% of pixels changed

If no motion: tick is skipped, no AI call is made.

---

## Step 4 — POI extraction (`PointOfInterestService`)

If `EnablePoiExtraction` is on, this turns the foreground mask into a set of cropped JPEG regions to send to AI instead of the full frame:

1. **Build a 32×24 hot-cell grid** — each cell covers a 5×5 pixel block in the 160×120 foreground. A cell is "hot" if enough of its pixels exceed the threshold (threshold is derived from `PoiSensitivity`).

2. **Mask out cells outside whitelist zones** (if configured).

3. **BFS flood-fill** to group connected hot cells into blobs. Uses 8-neighbor connectivity at sensitivity ≥ 0.3, otherwise 4-neighbor. Small blobs below `minCellCount` are discarded.

4. **Sort blobs by size, take the top 5.**

5. **Map each blob back to the full-resolution frame**: scales the grid coordinates back up, adds 40% padding around the tight bounding box, clamps the padded crop to 25% of the frame in each dimension (to avoid oversized crops), and caps at 640px on the longest side.

6. **Encode each crop as JPEG at 85% quality.**

If 0 regions are found: tick is skipped.

---

## Step 5 — Cooldown check

If a detection already happened recently (`DateTime.UtcNow < _cooldownUntil`), the AI call is skipped. The cooldown is set when a detection is saved and lasts `CooldownSeconds`.

---

## Step 6 — AI recognition (`ClaudeRecognitionService` / `GeminiRecognitionService`)

`_ai.RecognizeAsync(currentFrame, poiJpegs, ct)` is called.

**If POI crops exist** → sends each crop as a separate labelled image ("Region 1:", image, "Region 2:", image…) with a prompt asking to identify animals per region.

**If no POI crops** → falls back to the full frame, resizing it to max 1280px and compressing to JPEG 85% first.

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

## Step 7 — Filter results and save

For each `detected: true` result:

- Skip if `confidence < MinConfidenceThreshold`
- Set the cooldown (`_cooldownUntil = now + CooldownSeconds`)
- Call `_captureStorage.SaveCaptureAsync(frame, result, poiRegions, ...)` to persist the capture
- Fire `DetectionOccurred` event (used by the UI to show a notification)

---

## Summary

```
tick (every N seconds)
  → extract frame
  → update EMA background model
  → [gate: training complete?]
  → [gate: motion detected?]      ← MotionDetectionService on foreground mask
  → extract POI crops             ← PointOfInterestService BFS on hot-cell grid
  → [gate: POI count > 0?]
  → [gate: cooldown expired?]
  → send to AI (POI crops or full frame)
  → filter by confidence
  → save capture + fire event
```
