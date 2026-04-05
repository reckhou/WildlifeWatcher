using System.IO;
using SkiaSharp;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

/// <summary>
/// Finds motion hotspot regions using the EMA foreground mask,
/// then returns padded JPEG crops from the full-resolution current frame.
/// Grid dimensions are dynamic — set via Initialize() based on camera resolution and cell size.
/// </summary>
public class PointOfInterestService : IPointOfInterestService
{
    private const int CellPixelSize = 5; // pixels per cell in downscaled frame

    private int _gridCols;
    private int _gridRows;
    private int _scaleWidth;
    private int _scaleHeight;

    /// <inheritdoc/>
    public HotCellDebugData? LastHotCellDebug { get; private set; }

    private const double PadFraction      = 0.40; // padding added around tight bounding box
    private const double MaxBlobFraction  = 0.20; // skip tight bbox larger than 20% of frame in either dimension
    private const double MaxCropFraction  = 0.25; // clamp padded crop to 25% of frame in either dimension
    private const int    CropMaxDimension = 640;  // max side length of the crop sent to AI
    private const int    MinCropDimension = 128;  // min side length of the crop in full-res pixels
    private const int    DefaultMaxRegions = 5;    // default cap; overridable via settings

    /// <summary>
    /// Compute grid dimensions and downscale resolution for a given camera resolution and cell size.
    /// </summary>
    public static (int gridCols, int gridRows, int downscaleW, int downscaleH)
        ComputeGridDimensions(int cameraWidth, int cameraHeight, int cellSizePixels)
    {
        int gridCols = Math.Max(4, cameraWidth / cellSizePixels);
        int gridRows = Math.Max(3, cameraHeight / cellSizePixels);
        return (gridCols, gridRows, gridCols * CellPixelSize, gridRows * CellPixelSize);
    }

    public void Initialize(int gridCols, int gridRows)
    {
        _gridCols    = gridCols;
        _gridRows    = gridRows;
        _scaleWidth  = gridCols * CellPixelSize;
        _scaleHeight = gridRows * CellPixelSize;
    }

    public IReadOnlyList<PoiRegion> ExtractRegions(float[] foreground, byte[] currentFrame,
                                                     IReadOnlyList<MotionZone>? whitelistZones = null,
                                                     int pixelThreshold = 25,
                                                     double poiSensitivity = 0.5,
                                                     float[]? temporalDelta = null,
                                                     int temporalThreshold = 8,
                                                     double temporalCellFraction = 0.10,
                                                     int maxRegions = 5)
    {
        if (_gridCols == 0 || _gridRows == 0)
            return Array.Empty<PoiRegion>();

        // ── 0. Derive POI parameters from sensitivity ───────────────────────
        double cellHotFraction = 0.16 - 0.12 * poiSensitivity;       // 0.16 → 0.04
        int    minCellCount    = poiSensitivity >= 0.8 ? 1 : poiSensitivity >= 0.4 ? 2 : 3;
        bool   use8Neighbor    = poiSensitivity >= 0.3;

        // ── 1. Build hot-cell grid ──────────────────────────────────────────
        var hotCells = new bool[_gridRows, _gridCols];
        var debugState = new byte[_gridRows, _gridCols];
        const int cellPixels = CellPixelSize * CellPixelSize;
        for (int row = 0; row < _gridRows; row++)
        {
            for (int col = 0; col < _gridCols; col++)
            {
                int foregroundChanged = 0;
                int temporalChanged = 0;
                for (int py = 0; py < CellPixelSize; py++)
                for (int px = 0; px < CellPixelSize; px++)
                {
                    int idx = (row * CellPixelSize + py) * _scaleWidth + (col * CellPixelSize + px);
                    if (idx < foreground.Length && foreground[idx] > pixelThreshold) foregroundChanged++;
                    if (temporalDelta != null && idx < temporalDelta.Length && temporalDelta[idx] > temporalThreshold) temporalChanged++;
                }
                // Both conditions must be met: foreground diff AND temporal motion
                bool hasForeground = foregroundChanged >= cellPixels * cellHotFraction;
                bool hasTemporal = temporalDelta != null && temporalChanged >= cellPixels * temporalCellFraction;

                // Foreground-only zones: skip temporal requirement for cells inside them
                bool isForegroundOnlyZone = false;
                if (hasForeground && !hasTemporal && whitelistZones is { Count: > 0 })
                {
                    double nx = (col + 0.5) / _gridCols;
                    double ny = (row + 0.5) / _gridRows;
                    isForegroundOnlyZone = whitelistZones.Any(z =>
                        z.ForegroundOnly &&
                        nx >= z.NLeft && nx <= z.NLeft + z.NWidth &&
                        ny >= z.NTop  && ny <= z.NTop  + z.NHeight);
                }

                hotCells[row, col] = (hasForeground && hasTemporal) || isForegroundOnlyZone;
                // Debug: 0=cold, 1=foreground only, 2=temporal only, 3=both
                debugState[row, col] = (byte)((hasForeground ? 1 : 0) | (hasTemporal ? 2 : 0));
            }
        }
        LastHotCellDebug = new HotCellDebugData
        {
            GridCols = _gridCols, GridRows = _gridRows, CellState = debugState
        };

        // ── 1b. Mask hot cells outside whitelist zones ──────────────────────
        if (whitelistZones is { Count: > 0 })
        {
            for (int r = 0; r < _gridRows; r++)
            for (int c = 0; c < _gridCols; c++)
            {
                if (!hotCells[r, c]) continue;
                double nx = (c + 0.5) / _gridCols;
                double ny = (r + 0.5) / _gridRows;
                if (!whitelistZones.Any(z =>
                        nx >= z.NLeft && nx <= z.NLeft + z.NWidth &&
                        ny >= z.NTop  && ny <= z.NTop  + z.NHeight))
                    hotCells[r, c] = false;
            }
        }

        // ── 2. Direct zone crop + BFS ──────────────────────────────────────
        var visited = new bool[_gridRows, _gridCols];

        // 2a. ForegroundOnly zones: if ANY hot cell exists, crop the zone bounds
        //     directly — bypasses BFS entirely, so no blob merging / size filtering.
        var priorityZones = new List<MotionZone>();
        if (whitelistZones is { Count: > 0 })
        {
            foreach (var zone in whitelistZones.Where(z => z.ForegroundOnly))
            {
                // Floor for min, ceiling for max — include cells that partially overlap zone
                int zMinCol = Math.Clamp((int)Math.Floor(zone.NLeft * _gridCols), 0, _gridCols - 1);
                int zMaxCol = Math.Clamp((int)Math.Ceiling((zone.NLeft + zone.NWidth) * _gridCols) - 1, 0, _gridCols - 1);
                int zMinRow = Math.Clamp((int)Math.Floor(zone.NTop * _gridRows), 0, _gridRows - 1);
                int zMaxRow = Math.Clamp((int)Math.Ceiling((zone.NTop + zone.NHeight) * _gridRows) - 1, 0, _gridRows - 1);

                bool hasActivity = false;
                for (int r = zMinRow; r <= zMaxRow && !hasActivity; r++)
                for (int c = zMinCol; c <= zMaxCol && !hasActivity; c++)
                    if (hotCells[r, c]) hasActivity = true;

                if (hasActivity)
                {
                    priorityZones.Add(zone);
                    for (int r = zMinRow; r <= zMaxRow; r++)
                    for (int c = zMinCol; c <= zMaxCol; c++)
                    {
                        visited[r, c] = true;
                        if (debugState[r, c] > 0)
                            debugState[r, c] = 4;
                    }
                }
            }
        }

        // 2b. Normal BFS for remaining cells (non-ForegroundOnly zones)
        var normalComponents = new List<List<(int r, int c)>>();
        for (int r = 0; r < _gridRows; r++)
        for (int c = 0; c < _gridCols; c++)
        {
            if (!hotCells[r, c] || visited[r, c]) continue;

            var component = new List<(int, int)>();
            var queue     = new Queue<(int, int)>();
            queue.Enqueue((r, c));
            visited[r, c] = true;

            while (queue.Count > 0)
            {
                var (cr, cc) = queue.Dequeue();
                component.Add((cr, cc));

                int startDr = use8Neighbor ? -1 : 0;
                int endDr   = use8Neighbor ?  1 : 0;
                for (int dr = startDr; dr <= endDr; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    if (!use8Neighbor && dr != 0 && dc != 0) continue;
                    int nr = cr + dr, nc = cc + dc;
                    if (nr >= 0 && nr < _gridRows && nc >= 0 && nc < _gridCols &&
                        !visited[nr, nc] && hotCells[nr, nc])
                    {
                        visited[nr, nc] = true;
                        queue.Enqueue((nr, nc));
                    }
                }
            }

            if (component.Count >= minCellCount)
                normalComponents.Add(component);
        }

        if (priorityZones.Count == 0 && normalComponents.Count == 0)
            return Array.Empty<PoiRegion>();

        normalComponents.Sort((a, b) => b.Count.CompareTo(a.Count));
        int cap = Math.Clamp(maxRegions, 3, 10);

        // ── 3. Decode full-res frame once (SkiaSharp — no UI thread needed) ──
        using var fullRes = SKBitmap.Decode(currentFrame);
        if (fullRes == null)
            return Array.Empty<PoiRegion>();

        int imgW = fullRes.Width;
        int imgH = fullRes.Height;

        // ── 4. Create POI regions ──────────────────────────────────────────
        var regions = new List<PoiRegion>();

        // 4a. Priority: crop ForegroundOnly zones using exact normalized coords
        //     (no grid quantization, no padding, no MaxCropFraction clamp)
        foreach (var zone in priorityZones)
        {
            var region = CropZoneDirect(fullRes, imgW, imgH, zone, regions.Count + 1);
            if (region != null)
                regions.Add(region);
        }

        // 4b. Fill remaining slots with normal BFS blobs
        for (int i = 0; i < normalComponents.Count && regions.Count < cap; i++)
        {
            var comp   = normalComponents[i];
            int minRow = comp.Min(x => x.r);
            int maxRow = comp.Max(x => x.r);
            int minCol = comp.Min(x => x.c);
            int maxCol = comp.Max(x => x.c);

            var region = CropRegionFromComponent(fullRes, imgW, imgH,
                minRow, maxRow, minCol, maxCol, regions.Count + 1);
            if (region != null)
                regions.Add(region);
        }

        // ── 5. Merge overlapping regions (>40% overlap by smaller area) ────
        MergeOverlappingRegions(regions, fullRes, imgW, imgH);

        return regions;
    }

    /// <summary>
    /// Crop a region from a full-resolution frame, given grid-space bounding box.
    /// Reuses existing padding/clamping/encoding logic.
    /// </summary>
    public PoiRegion? CropRegion(byte[] frame, int minRow, int maxRow, int minCol, int maxCol, int index,
        double maxBlobFrac = MaxBlobFraction, double padFraction = PadFraction)
    {
        using var fullRes = SKBitmap.Decode(frame);
        if (fullRes == null) return null;
        return CropRegionFromComponent(fullRes, fullRes.Width, fullRes.Height, minRow, maxRow, minCol, maxCol, index, maxBlobFrac, padFraction);
    }

    private PoiRegion? CropRegionFromComponent(SKBitmap fullRes, int imgW, int imgH,
        int minRow, int maxRow, int minCol, int maxCol, int index,
        double maxBlobFrac = MaxBlobFraction, double padFraction = PadFraction)
    {
        double scaleX = (double)imgW / _scaleWidth;
        double scaleY = (double)imgH / _scaleHeight;

        double x1 = minCol * CellPixelSize * scaleX;
        double y1 = minRow * CellPixelSize * scaleY;
        double x2 = (maxCol + 1) * CellPixelSize * scaleX;
        double y2 = (maxRow + 1) * CellPixelSize * scaleY;

        double padX = (x2 - x1) * padFraction;
        double padY = (y2 - y1) * padFraction;

        int cx1 = (int)Math.Max(0,    x1 - padX);
        int cy1 = (int)Math.Max(0,    y1 - padY);
        int cx2 = (int)Math.Min(imgW, x2 + padX);
        int cy2 = (int)Math.Min(imgH, y2 + padY);

        int cropW = cx2 - cx1;
        int cropH = cy2 - cy1;

        // Enforce minimum crop size, extending from tight bbox centre
        double blobCenterX = (x1 + x2) / 2.0;
        double blobCenterY = (y1 + y2) / 2.0;
        if (cropW < MinCropDimension)
        {
            cx1   = (int)Math.Max(0,    blobCenterX - MinCropDimension / 2.0);
            cx2   = Math.Min(imgW, cx1 + MinCropDimension);
            cx1   = Math.Max(0, cx2 - MinCropDimension); // re-adjust if clamped at right edge
            cropW = cx2 - cx1;
        }
        if (cropH < MinCropDimension)
        {
            cy1   = (int)Math.Max(0,    blobCenterY - MinCropDimension / 2.0);
            cy2   = Math.Min(imgH, cy1 + MinCropDimension);
            cy1   = Math.Max(0, cy2 - MinCropDimension); // re-adjust if clamped at bottom edge
            cropH = cy2 - cy1;
        }
        if (cropW < 20 || cropH < 20) return null;

        // Skip blobs whose tight bounding box is too large to be a single bird
        double tightW = x2 - x1;
        double tightH = y2 - y1;
        if (tightW > imgW * maxBlobFrac || tightH > imgH * maxBlobFrac) return null;

        // Clamp padded crop to max size, centered on the tight bbox centroid
        int maxCropW = (int)(imgW * MaxCropFraction);
        int maxCropH = (int)(imgH * MaxCropFraction);
        if (cropW > maxCropW)
        {
            cx1   = (int)Math.Max(0,    blobCenterX - maxCropW / 2.0);
            cx2   = Math.Min(imgW, cx1 + maxCropW);
            cropW = cx2 - cx1;
        }
        if (cropH > maxCropH)
        {
            cy1   = (int)Math.Max(0,    blobCenterY - maxCropH / 2.0);
            cy2   = Math.Min(imgH, cy1 + maxCropH);
            cropH = cy2 - cy1;
        }

        byte[] croppedJpeg = CropAndEncode(fullRes, cx1, cy1, cropW, cropH);
        return new PoiRegion(
            (double)cx1 / imgW, (double)cy1 / imgH,
            (double)cropW / imgW, (double)cropH / imgH,
            croppedJpeg, index);
    }

    // ── Heatmap methods (for burst mode) ────────────────────────────────────

    /// <summary>
    /// Compute the hot-cell grid for a single frame with intensity weighting.
    /// Returns a 2D array where 0 = cold, positive = average foreground intensity of hot pixels.
    /// </summary>
    public float[,] ComputeHotCellGrid(float[] foreground, float[]? temporalDelta,
        IReadOnlyList<MotionZone>? whitelistZones,
        int pixelThreshold, double poiSensitivity,
        int temporalThreshold, double temporalCellFraction)
    {
        double cellHotFraction = 0.16 - 0.12 * poiSensitivity;
        var grid = new float[_gridRows, _gridCols];
        const int cellPixels = CellPixelSize * CellPixelSize;

        for (int row = 0; row < _gridRows; row++)
        {
            for (int col = 0; col < _gridCols; col++)
            {
                int foregroundChanged = 0;
                int temporalChanged = 0;
                float intensitySum = 0;
                for (int py = 0; py < CellPixelSize; py++)
                for (int px = 0; px < CellPixelSize; px++)
                {
                    int idx = (row * CellPixelSize + py) * _scaleWidth + (col * CellPixelSize + px);
                    if (idx < foreground.Length)
                    {
                        if (foreground[idx] > pixelThreshold)
                        {
                            foregroundChanged++;
                            intensitySum += foreground[idx];
                        }
                    }
                    if (temporalDelta != null && idx < temporalDelta.Length && temporalDelta[idx] > temporalThreshold)
                        temporalChanged++;
                }

                bool hasForeground = foregroundChanged >= cellPixels * cellHotFraction;
                bool hasTemporal = temporalDelta != null && temporalChanged >= cellPixels * temporalCellFraction;

                bool isForegroundOnlyZone = false;
                if (hasForeground && !hasTemporal && whitelistZones is { Count: > 0 })
                {
                    double nx = (col + 0.5) / _gridCols;
                    double ny = (row + 0.5) / _gridRows;
                    isForegroundOnlyZone = whitelistZones.Any(z =>
                        z.ForegroundOnly &&
                        nx >= z.NLeft && nx <= z.NLeft + z.NWidth &&
                        ny >= z.NTop  && ny <= z.NTop  + z.NHeight);
                }

                if (((hasForeground && hasTemporal) || isForegroundOnlyZone) && foregroundChanged > 0)
                    grid[row, col] = intensitySum / foregroundChanged;
            }
        }

        // Mask outside whitelist zones
        if (whitelistZones is { Count: > 0 })
        {
            for (int r = 0; r < _gridRows; r++)
            for (int c = 0; c < _gridCols; c++)
            {
                if (grid[r, c] <= 0) continue;
                double nx = (c + 0.5) / _gridCols;
                double ny = (r + 0.5) / _gridRows;
                if (!whitelistZones.Any(z =>
                        nx >= z.NLeft && nx <= z.NLeft + z.NWidth &&
                        ny >= z.NTop  && ny <= z.NTop  + z.NHeight))
                    grid[r, c] = 0;
            }
        }

        return grid;
    }

    /// <summary>
    /// Accumulate a hot-cell grid into a running heatmap (element-wise addition).
    /// </summary>
    public void AccumulateHeatmap(float[,] heatmap, float[,] hotCells)
    {
        for (int r = 0; r < _gridRows; r++)
        for (int c = 0; c < _gridCols; c++)
            heatmap[r, c] += hotCells[r, c];
    }

    /// <summary>
    /// Extract POI regions from an accumulated heatmap. Thresholds cells by minimum
    /// hit count, runs BFS, returns grid-space bounding boxes with peak intensity.
    /// </summary>
    public IReadOnlyList<(int minRow, int maxRow, int minCol, int maxCol, float peakIntensity)>
        ExtractHeatmapRegions(float[,] heatmap, int minHitCount, double poiSensitivity, int maxRegions = 5)
    {
        int minCellCount = poiSensitivity >= 0.8 ? 1 : poiSensitivity >= 0.4 ? 2 : 3;
        bool use8Neighbor = poiSensitivity >= 0.3;

        // Threshold: cell must have accumulated intensity from at least minHitCount frames
        // We treat any cell with heatmap > 0 as "hit at least once"; for minHitCount > 1,
        // we need the average intensity × hitCount to exceed a threshold.
        // Simpler: since we accumulate average-intensity per frame, a cell hot in N frames
        // will have sum ≈ N * avgIntensity. We threshold on the sum being > minHitCount * some base.
        // For simplicity: count how many frames contributed by checking if value > 0.
        // Actually, the plan says minHitCount = how many frames a cell must be hot in.
        // We don't track frame count per cell directly, but since intensities are > 0 only when hot,
        // we can approximate: value > minHitCount * minimumDetectableIntensity.
        // Let's use a simple threshold: value must be >= minHitCount * 5 (arbitrary low bar).
        float threshold = minHitCount * 5f;

        var active = new bool[_gridRows, _gridCols];
        for (int r = 0; r < _gridRows; r++)
        for (int c = 0; c < _gridCols; c++)
            active[r, c] = heatmap[r, c] >= threshold;

        // BFS
        var visited = new bool[_gridRows, _gridCols];
        var results = new List<(int, int, int, int, float)>();

        for (int r = 0; r < _gridRows; r++)
        for (int c = 0; c < _gridCols; c++)
        {
            if (!active[r, c] || visited[r, c]) continue;

            var component = new List<(int r, int c)>();
            var queue = new Queue<(int, int)>();
            queue.Enqueue((r, c));
            visited[r, c] = true;

            while (queue.Count > 0)
            {
                var (cr, cc) = queue.Dequeue();
                component.Add((cr, cc));

                int startDr = use8Neighbor ? -1 : 0;
                int endDr = use8Neighbor ? 1 : 0;
                for (int dr = startDr; dr <= endDr; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    if (!use8Neighbor && dr != 0 && dc != 0) continue;
                    int nr = cr + dr, nc = cc + dc;
                    if (nr >= 0 && nr < _gridRows && nc >= 0 && nc < _gridCols &&
                        !visited[nr, nc] && active[nr, nc])
                    {
                        visited[nr, nc] = true;
                        queue.Enqueue((nr, nc));
                    }
                }
            }

            if (component.Count >= minCellCount)
            {
                int minRow = component.Min(x => x.r);
                int maxRow = component.Max(x => x.r);
                int minCol = component.Min(x => x.c);
                int maxCol = component.Max(x => x.c);
                float peak = component.Max(x => heatmap[x.r, x.c]);

                // Skip if tight bbox is too large
                double tightWFrac = (double)(maxCol - minCol + 1) / _gridCols;
                double tightHFrac = (double)(maxRow - minRow + 1) / _gridRows;
                if (tightWFrac <= MaxBlobFraction && tightHFrac <= MaxBlobFraction)
                    results.Add((minRow, maxRow, minCol, maxCol, peak));
            }
        }

        results.Sort((a, b) => b.Item5.CompareTo(a.Item5));
        int cap = Math.Clamp(maxRegions, 3, 10);
        if (results.Count > cap)
            results.RemoveRange(cap, results.Count - cap);

        return results;
    }

    /// <summary>
    /// Crop a region using the zone's exact normalized coordinates — no grid
    /// quantization, no padding, no MaxCropFraction clamp.
    /// </summary>
    public PoiRegion? CropZoneDirect(byte[] frame, MotionZone zone, int index)
    {
        using var fullRes = SKBitmap.Decode(frame);
        if (fullRes == null) return null;
        return CropZoneDirect(fullRes, fullRes.Width, fullRes.Height, zone, index);
    }

    private PoiRegion? CropZoneDirect(SKBitmap fullRes, int imgW, int imgH, MotionZone zone, int index)
    {
        int cx = Math.Clamp((int)(zone.NLeft * imgW), 0, imgW - 1);
        int cy = Math.Clamp((int)(zone.NTop * imgH), 0, imgH - 1);
        int cw = Math.Min((int)(zone.NWidth * imgW), imgW - cx);
        int ch = Math.Min((int)(zone.NHeight * imgH), imgH - cy);
        if (cw < 20 || ch < 20) return null;

        byte[] jpeg = CropAndEncode(fullRes, cx, cy, cw, ch);
        return new PoiRegion(
            (double)cx / imgW, (double)cy / imgH,
            (double)cw / imgW, (double)ch / imgH,
            jpeg, index);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const double MergeOverlapThreshold = 0.40;

    /// <summary>
    /// Merge regions that overlap by more than 40% (intersection / smaller area).
    /// Mutates the list in-place: merged pairs become a single union-bbox crop.
    /// </summary>
    private static void MergeOverlappingRegions(List<PoiRegion> regions, SKBitmap fullRes, int imgW, int imgH)
    {
        if (regions.Count < 2) return;

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < regions.Count && !merged; i++)
            for (int j = i + 1; j < regions.Count && !merged; j++)
            {
                var a = regions[i];
                var b = regions[j];

                double intL = Math.Max(a.NLeft, b.NLeft);
                double intT = Math.Max(a.NTop, b.NTop);
                double intR = Math.Min(a.NLeft + a.NWidth, b.NLeft + b.NWidth);
                double intB = Math.Min(a.NTop + a.NHeight, b.NTop + b.NHeight);
                if (intR <= intL || intB <= intT) continue;

                double intArea = (intR - intL) * (intB - intT);
                double smaller = Math.Min(a.NWidth * a.NHeight, b.NWidth * b.NHeight);
                if (intArea / smaller < MergeOverlapThreshold) continue;

                // Union bounding box
                double uL = Math.Min(a.NLeft, b.NLeft);
                double uT = Math.Min(a.NTop, b.NTop);
                double uR = Math.Max(a.NLeft + a.NWidth, b.NLeft + b.NWidth);
                double uB = Math.Max(a.NTop + a.NHeight, b.NTop + b.NHeight);

                int cx = Math.Max(0, (int)(uL * imgW));
                int cy = Math.Max(0, (int)(uT * imgH));
                int cw = Math.Min(imgW - cx, (int)((uR - uL) * imgW));
                int ch = Math.Min(imgH - cy, (int)((uB - uT) * imgH));
                if (cw < 20 || ch < 20) continue;

                byte[] jpeg = CropAndEncode(fullRes, cx, cy, cw, ch);
                var union = new PoiRegion(
                    (double)cx / imgW, (double)cy / imgH,
                    (double)cw / imgW, (double)ch / imgH,
                    jpeg, Math.Min(a.Index, b.Index));

                regions.RemoveAt(j);
                regions[i] = union;
                merged = true;
            }
        }

        // Re-index
        for (int i = 0; i < regions.Count; i++)
            regions[i] = regions[i] with { Index = i + 1 };
    }

    private static byte[] CropAndEncode(SKBitmap source, int x, int y, int w, int h)
    {
        var subset = new SKRectI(x, y, x + w, y + h);
        using var cropped = new SKBitmap();
        source.ExtractSubset(cropped, subset);

        SKBitmap toEncode = cropped;
        SKBitmap? scaled  = null;
        try
        {
            if (w > CropMaxDimension || h > CropMaxDimension)
            {
                var scale = Math.Min((double)CropMaxDimension / w, (double)CropMaxDimension / h);
                int newW  = (int)(w * scale);
                int newH  = (int)(h * scale);
                scaled    = cropped.Resize(new SKSizeI(newW, newH), SKSamplingOptions.Default);
                toEncode  = scaled;
            }

            using var image = SKImage.FromBitmap(toEncode);
            using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }
}
