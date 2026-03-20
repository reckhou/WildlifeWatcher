using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

/// <summary>
/// Finds motion hotspot regions using the EMA foreground mask,
/// then returns padded JPEG crops from the full-resolution current frame.
/// Grid is 32×24 (5×5 px cells at 160×120) for tighter isolation of small subjects.
/// </summary>
public class PointOfInterestService : IPointOfInterestService
{
    // Downscale resolution — 32×24 cells of 5×5 pixels
    private const int GridCols    = 32;
    private const int GridRows    = 24;
    private const int ScaleWidth  = GridCols * 5; // 160
    private const int ScaleHeight = GridRows * 5; // 120

    private const int    PixelThreshold   = 25;   // foreground intensity to count a pixel as changed
    private const double CellHotFraction  = 0.06; // fraction of cell pixels that must be foreground
    private const int    MinCellCount     = 1;    // minimum hot cells to form a region
    private const double PadFraction      = 0.40; // padding added around tight bounding box
    private const int    CropMaxDimension = 640;  // max side length of the crop sent to AI
    private const int    MaxRegions       = 5;    // cap to avoid sending too many images

    public IReadOnlyList<PoiRegion> ExtractRegions(float[] foreground, byte[] currentFrame)
    {
        // ── 1. Build hot-cell grid ──────────────────────────────────────────
        var hotCells = new bool[GridRows, GridCols];
        const int cellPixels = 5 * 5;
        for (int row = 0; row < GridRows; row++)
        {
            for (int col = 0; col < GridCols; col++)
            {
                int changed = 0;
                for (int py = 0; py < 5; py++)
                for (int px = 0; px < 5; px++)
                {
                    int idx = (row * 5 + py) * ScaleWidth + (col * 5 + px);
                    if (foreground[idx] > PixelThreshold) changed++;
                }
                hotCells[row, col] = changed >= cellPixels * CellHotFraction;
            }
        }

        // ── 2. BFS connected components ────────────────────────────────────
        var visited    = new bool[GridRows, GridCols];
        var components = new List<List<(int r, int c)>>();

        for (int r = 0; r < GridRows; r++)
        for (int c = 0; c < GridCols; c++)
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
                foreach (var (nr, nc) in Neighbors(cr, cc))
                {
                    if (!visited[nr, nc] && hotCells[nr, nc])
                    {
                        visited[nr, nc] = true;
                        queue.Enqueue((nr, nc));
                    }
                }
            }

            if (component.Count >= MinCellCount)
                components.Add(component);
        }

        if (components.Count == 0)
            return Array.Empty<PoiRegion>();

        // Sort by size descending, take top N
        components.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (components.Count > MaxRegions)
            components.RemoveRange(MaxRegions, components.Count - MaxRegions);

        // ── 3. Decode full-res frame once ───────────────────────────────────
        BitmapSource fullRes;
        using (var ms = new MemoryStream(currentFrame))
            fullRes = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        int imgW = fullRes.PixelWidth;
        int imgH = fullRes.PixelHeight;

        // ── 4. Map each component to a padded crop ──────────────────────────
        var regions = new List<PoiRegion>(components.Count);

        for (int i = 0; i < components.Count; i++)
        {
            var comp   = components[i];
            int minRow = comp.Min(x => x.r);
            int maxRow = comp.Max(x => x.r);
            int minCol = comp.Min(x => x.c);
            int maxCol = comp.Max(x => x.c);

            double scaleX = (double)imgW / ScaleWidth;
            double scaleY = (double)imgH / ScaleHeight;

            double x1 = minCol * 5 * scaleX;
            double y1 = minRow * 5 * scaleY;
            double x2 = (maxCol + 1) * 5 * scaleX;
            double y2 = (maxRow + 1) * 5 * scaleY;

            double padX = (x2 - x1) * PadFraction;
            double padY = (y2 - y1) * PadFraction;

            int cx1 = (int)Math.Max(0,    x1 - padX);
            int cy1 = (int)Math.Max(0,    y1 - padY);
            int cx2 = (int)Math.Min(imgW, x2 + padX);
            int cy2 = (int)Math.Min(imgH, y2 + padY);

            int cropW = cx2 - cx1;
            int cropH = cy2 - cy1;
            if (cropW < 20 || cropH < 20) continue;

            byte[] croppedJpeg = CropAndEncode(fullRes, cx1, cy1, cropW, cropH);
            regions.Add(new PoiRegion(
                (double)cx1 / imgW, (double)cy1 / imgH,
                (double)cropW / imgW, (double)cropH / imgH,
                croppedJpeg, i + 1));
        }

        return regions;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] CropAndEncode(BitmapSource source, int x, int y, int w, int h)
    {
        var cropped  = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        BitmapSource toEncode = cropped;

        if (w > CropMaxDimension || h > CropMaxDimension)
        {
            var scale = Math.Min((double)CropMaxDimension / w, (double)CropMaxDimension / h);
            toEncode  = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(toEncode));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static IEnumerable<(int r, int c)> Neighbors(int r, int c)
    {
        if (r > 0)            yield return (r - 1, c);
        if (r < GridRows - 1) yield return (r + 1, c);
        if (c > 0)            yield return (r, c - 1);
        if (c < GridCols - 1) yield return (r, c + 1);
    }
}
