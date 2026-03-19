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
}
