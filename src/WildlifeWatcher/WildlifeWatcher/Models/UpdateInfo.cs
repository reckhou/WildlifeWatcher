namespace WildlifeWatcher.Models;

public class UpdateInfo
{
    public Version CurrentVersion { get; init; } = new();
    public Version LatestVersion  { get; init; } = new();
    public string  TagName        { get; init; } = string.Empty;
    public string  DownloadUrl    { get; init; } = string.Empty;
    public string  Sha256         { get; init; } = string.Empty;
    public string  ReleaseNotes   { get; init; } = string.Empty;

    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}
