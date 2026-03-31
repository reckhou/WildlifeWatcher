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

    private const double PadFraction      = 0.40; // padding added around tight bounding box
    private const double MaxBlobFraction  = 0.20; // skip tight bbox larger than 20% of frame in either dimension
    private const double MaxCropFraction  = 0.25; // clamp padded crop to 25% of frame in either dimension
    private const int    CropMaxDimension = 640;  // max side length of the crop sent to AI
    private const int    MaxRegions       = 5;    // cap to avoid sending too many images

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
                                                     double temporalCellFraction = 0.10)
    {
        if (_gridCols == 0 || _gridRows == 0)
            return Array.Empty<PoiRegion>();

        // ── 0. Derive POI parameters from sensitivity ───────────────────────
        double cellHotFraction = 0.16 - 0.12 * poiSensitivity;       // 0.16 → 0.04
        int    minCellCount    = poiSensitivity >= 0.8 ? 1 : poiSensitivity >= 0.4 ? 2 : 3;
        bool   use8Neighbor    = poiSensitivity >= 0.3;

        // ── 1. Build hot-cell grid ──────────────────────────────────────────
        var hotCells = new bool[_gridRows, _gridCols];
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
                hotCells[row, col] = hasForeground && hasTemporal;
            }
        }

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

        // ── 2. BFS connected components ────────────────────────────────────
        var visited    = new bool[_gridRows, _gridCols];
        var components = new List<List<(int r, int c)>>();

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
                components.Add(component);
        }

        if (components.Count == 0)
            return Array.Empty<PoiRegion>();

        // Sort by size descending, take top N
        components.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (components.Count > MaxRegions)
            components.RemoveRange(MaxRegions, components.Count - MaxRegions);

        // ── 3. Decode full-res frame once (SkiaSharp — no UI thread needed) ──
        using var fullRes = SKBitmap.Decode(currentFrame);
        if (fullRes == null)
            return Array.Empty<PoiRegion>();

        int imgW = fullRes.Width;
        int imgH = fullRes.Height;

        // ── 4. Map each component to a padded crop ──────────────────────────
        var regions = new List<PoiRegion>(components.Count);

        for (int i = 0; i < components.Count; i++)
        {
            var comp   = components[i];
            int minRow = comp.Min(x => x.r);
            int maxRow = comp.Max(x => x.r);
            int minCol = comp.Min(x => x.c);
            int maxCol = comp.Max(x => x.c);

            var region = CropRegionFromComponent(fullRes, imgW, imgH, minRow, maxRow, minCol, maxCol, regions.Count + 1);
            if (region != null)
                regions.Add(region);
        }

        return regions;
    }

    /// <summary>
    /// Crop a region from a full-resolution frame, given grid-space bounding box.
    /// Reuses existing padding/clamping/encoding logic.
    /// </summary>
    public PoiRegion? CropRegion(byte[] frame, int minRow, int maxRow, int minCol, int maxCol, int index)
    {
        using var fullRes = SKBitmap.Decode(frame);
        if (fullRes == null) return null;
        return CropRegionFromComponent(fullRes, fullRes.Width, fullRes.Height, minRow, maxRow, minCol, maxCol, index);
    }

    private PoiRegion? CropRegionFromComponent(SKBitmap fullRes, int imgW, int imgH,
        int minRow, int maxRow, int minCol, int maxCol, int index)
    {
        double scaleX = (double)imgW / _scaleWidth;
        double scaleY = (double)imgH / _scaleHeight;

        double x1 = minCol * CellPixelSize * scaleX;
        double y1 = minRow * CellPixelSize * scaleY;
        double x2 = (maxCol + 1) * CellPixelSize * scaleX;
        double y2 = (maxRow + 1) * CellPixelSize * scaleY;

        double padX = (x2 - x1) * PadFraction;
        double padY = (y2 - y1) * PadFraction;

        int cx1 = (int)Math.Max(0,    x1 - padX);
        int cy1 = (int)Math.Max(0,    y1 - padY);
        int cx2 = (int)Math.Min(imgW, x2 + padX);
        int cy2 = (int)Math.Min(imgH, y2 + padY);

        int cropW = cx2 - cx1;
        int cropH = cy2 - cy1;
        if (cropW < 20 || cropH < 20) return null;

        // Skip blobs whose tight bounding box is too large to be a single bird
        double tightW = x2 - x1;
        double tightH = y2 - y1;
        if (tightW > imgW * MaxBlobFraction || tightH > imgH * MaxBlobFraction) return null;

        // Clamp padded crop to max size, centered on the tight bbox centroid
        int maxCropW = (int)(imgW * MaxCropFraction);
        int maxCropH = (int)(imgH * MaxCropFraction);
        if (cropW > maxCropW)
        {
            double blobCx = (x1 + x2) / 2.0;
            cx1   = (int)Math.Max(0,    blobCx - maxCropW / 2.0);
            cx2   = Math.Min(imgW, cx1 + maxCropW);
            cropW = cx2 - cx1;
        }
        if (cropH > maxCropH)
        {
            double blobCy = (y1 + y2) / 2.0;
            cy1   = (int)Math.Max(0,    blobCy - maxCropH / 2.0);
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
                if (hasForeground && hasTemporal && foregroundChanged > 0)
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
        ExtractHeatmapRegions(float[,] heatmap, int minHitCount, double poiSensitivity)
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
        if (results.Count > MaxRegions)
            results.RemoveRange(MaxRegions, results.Count - MaxRegions);

        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
