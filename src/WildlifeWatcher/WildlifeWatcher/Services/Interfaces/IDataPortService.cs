namespace WildlifeWatcher.Services.Interfaces;

public interface IDataPortService
{
    Task ExportAsync(string zipPath, IProgress<(int percent, string message)> progress, CancellationToken ct);
    Task ImportAsync(string zipPath, string preserveRtspUrl, IProgress<(int percent, string message)> progress, CancellationToken ct);
}
