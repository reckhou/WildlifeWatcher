# Decision Log: POI Burst Mode & Pipeline Improvements

## Brainstorming Phase — 2026-03-31

### Decision: Scope — Foundation
- **Chosen:** Foundation (core pipeline feature)
- **Alternatives considered:** Throwaway spike
- **Rationale:** Burst mode integrates deeply into the recognition pipeline and will be the primary detection path going forward
- **Trade-offs accepted:** More upfront design work; can't just hack it in

### Decision: Algorithm — Heatmap Accumulation
- **Chosen:** Accumulate hot-cell grids across burst frames into a shared heatmap, then BFS to extract busiest regions
- **Alternatives considered:** IoU-based merging (medium complexity, fragile at low FPS), Weighted Box Fusion (good but designed for model ensembles), DBSCAN clustering (only uses centers, ignores box size), Temporal decay weighting (add-on only, too short a window)
- **Rationale:** Heatmap reuses the existing 32×24 hot-cell grid infrastructure and BFS extraction. Directly answers "busiest area" with minimal new code. No box-to-box association logic needed.
- **Trade-offs accepted:** Doesn't preserve per-box identity (acceptable since the final crop is taken from the best representative frame)

### Decision: EMA Background Model — Make Standalone
- **Chosen:** Decouple EMA updates to their own timer (default 2s), independent of detection loop
- **Alternatives considered:** Keep coupled to detection tick (status quo)
- **Rationale:** At 30s intervals, training takes ~30 minutes and adaptation to lighting changes is sluggish. Standalone timer at 2s brings training to ~2 minutes and keeps background fresh. Also cleanly separates "update background" from "compute foreground for a specific frame", which the burst mode needs.
- **Trade-offs accepted:** Thread safety requires lock around background array; slight increase in frame extraction frequency (but 160×120 grayscale processing is negligible)

### Decision: EMA Not Updated During Burst
- **Chosen:** Burst frames compute foreground against the existing background but don't update the EMA
- **Alternatives considered:** Feeding burst frames into the EMA
- **Rationale:** 10 rapid frames would shift the EMA significantly toward the current scene, potentially suppressing the motion signal that triggered the burst in the first place
- **Trade-offs accepted:** Background model is slightly less current after a burst (negligible given 2s standalone updates)

### Decision: Grid Resolution — Dynamic with Presets
- **Chosen:** Configurable cell size (20–80px, step 10) with 7 named presets, auto-derived grid dimensions from camera resolution
- **Alternatives considered:** Fixed 2× grid (64×48), fixed 3× grid (96×72), raw numeric input
- **Rationale:** A 2K camera with the current 32×24 grid has 80×60px cells — a small bird (40–80px) fits in one cell and gets discarded by minCellCount. Dynamic sizing solves this for all camera resolutions. Presets with descriptive names are more user-friendly than raw pixel values.
- **Trade-offs accepted:** BackgroundModelService resolution must also be dynamic (touched anyway for standalone refactor)

### Decision: Crop Selection — From Best Frame Per Region
- **Chosen:** For each burst-derived region, crop from the frame where that region's heatmap contribution was strongest
- **Alternatives considered:** Crop from the last frame, crop from the first frame, crop from a composite
- **Rationale:** The frame with the strongest signal for a given region likely has the subject most clearly visible/centered in that area

### Decision: VLC OSD — Disable via --no-osd
- **Chosen:** Pass `--no-osd` to LibVLC initialization
- **Alternatives considered:** Disabling marquee per-snapshot, suppressing via video filter
- **Rationale:** Simplest global fix — one flag at init time. No per-snapshot logic needed.

### Decision: source_crop_index Fallback — Use First Region
- **Chosen:** When AI omits source_crop_index but POI crops were sent, fall back to region 1 (largest by BFS sort order)
- **Alternatives considered:** Discard the detection entirely, pick a random region, send the detection with no POI data
- **Rationale:** The largest region (first by sort order) is the most likely candidate. Discarding would lose valid detections. Logging the fallback makes the issue visible for debugging.

## Planning Phase — 2026-03-31

### Decision: Split ProcessFrame into UpdateBackground + ComputeForeground
- **Chosen:** Two methods — one mutates the model (timer-driven), one is read-only (on-demand)
- **Alternatives considered:** Single ProcessFrame with a "don't update EMA" flag
- **Rationale:** Clean separation of concerns. ComputeForeground returns new arrays, eliminating thread safety concerns on the output side. The burst loop needs its own previousGray tracking (frame-to-frame within burst), which maps naturally to ComputeForeground accepting previousGray as a parameter.

### Decision: ComputeForeground Returns Gray Pixels
- **Chosen:** Return tuple includes raw gray pixels alongside foreground and temporal delta
- **Alternatives considered:** Separate ToGrayPixels public method
- **Rationale:** ComputeForeground already decodes the PNG to grayscale internally. Returning the gray pixels avoids redundant decoding and gives the burst loop its previousGray for the next iteration with zero extra cost.
