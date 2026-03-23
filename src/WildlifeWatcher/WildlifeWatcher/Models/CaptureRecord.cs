namespace WildlifeWatcher.Models;

public class CaptureRecord
{
    public int Id { get; set; }
    public int SpeciesId { get; set; }
    public Species Species { get; set; } = null!;
    public DateTime CapturedAt { get; set; }
    public string ImageFilePath { get; set; } = string.Empty;
    public string AiRawResponse { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public string? Notes { get; set; }
    public string? AnnotatedImageFilePath { get; set; }
    public string? AlternativesJson { get; set; }

    // Source POI bounding box (normalized 0..1). Null if full frame was analyzed.
    public double? PoiNLeft   { get; set; }
    public double? PoiNTop    { get; set; }
    public double? PoiNWidth  { get; set; }
    public double? PoiNHeight { get; set; }

    // Weather data captured at detection time (Phase 6)
    public double?   Temperature      { get; set; }
    public string?   WeatherCondition { get; set; }
    public double?   WindSpeed        { get; set; }
    public double?   Precipitation    { get; set; }
    public DateTime? Sunrise          { get; set; }
    public DateTime? Sunset           { get; set; }
}
