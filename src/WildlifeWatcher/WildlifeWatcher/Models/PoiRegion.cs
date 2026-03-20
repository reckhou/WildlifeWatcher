namespace WildlifeWatcher.Models;

/// <summary>A motion hotspot region extracted from the current frame.</summary>
/// <param name="NLeft">Normalized left edge (0..1).</param>
/// <param name="NTop">Normalized top edge (0..1).</param>
/// <param name="NWidth">Normalized width (0..1).</param>
/// <param name="NHeight">Normalized height (0..1).</param>
/// <param name="CroppedJpeg">JPEG bytes of the cropped region, resized to ≤640 px.</param>
/// <param name="Index">1-based index within the current frame analysis.</param>
public record PoiRegion(double NLeft, double NTop, double NWidth, double NHeight, byte[] CroppedJpeg, int Index);
