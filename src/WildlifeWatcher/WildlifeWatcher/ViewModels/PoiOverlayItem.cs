using WildlifeWatcher.Models;

namespace WildlifeWatcher.ViewModels;

/// <summary>
/// One POI rectangle in a fixed 1600×900 virtual canvas space.
/// Used by the Viewbox overlay in LiveViewPage.
/// </summary>
public class PoiOverlayItem
{
    private const double VW = 1600;
    private const double VH = 900;

    public double CanvasLeft   { get; init; }
    public double CanvasTop    { get; init; }
    public double CanvasWidth  { get; init; }
    public double CanvasHeight { get; init; }
    public string Label        { get; init; } = string.Empty;

    public static PoiOverlayItem FromRegion(PoiRegion r) => new()
    {
        CanvasLeft   = r.NLeft   * VW,
        CanvasTop    = r.NTop    * VH,
        CanvasWidth  = r.NWidth  * VW,
        CanvasHeight = r.NHeight * VH,
        Label        = $"#{r.Index}"
    };
}
