namespace WildlifeWatcher.Models;

public class Species
{
    public int Id { get; set; }
    public string CommonName { get; set; } = string.Empty;
    public string ScientificName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime FirstDetectedAt { get; set; }
    public ICollection<CaptureRecord> Captures { get; set; } = new List<CaptureRecord>();
}
