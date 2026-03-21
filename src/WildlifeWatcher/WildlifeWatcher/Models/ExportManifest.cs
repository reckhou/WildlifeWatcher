namespace WildlifeWatcher.Models;

public class ExportManifest
{
    public string AppVersion { get; set; } = string.Empty;
    public DateTime ExportedAt { get; set; }
    public string OriginalBasePath { get; set; } = string.Empty;
}
