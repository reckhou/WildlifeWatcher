namespace WildlifeWatcher.Models;

public class ImportJob
{
    public string ZipPath { get; set; } = string.Empty;
    public string PreserveRtspUrl { get; set; } = string.Empty;
    public int MainProcessId { get; set; }
}
