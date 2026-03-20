using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class UpdateService : IUpdateService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService   _settings;

    public UpdateService(IHttpClientFactory httpFactory, ISettingsService settings)
    {
        _httpFactory = httpFactory;
        _settings    = settings;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

        if (_settings.CurrentSettings.DebugForceUpdateAvailable)
            return new UpdateInfo
            {
                CurrentVersion = current,
                LatestVersion  = new Version(99, 0, 0),
                TagName        = "v99.0.0",
                DownloadUrl    = string.Empty,
                ReleaseNotes   = "Debug fake update"
            };

        try
        {
            var http     = _httpFactory.CreateClient("github");
            var response = await http.GetAsync(
                "repos/reckhou/WildlifeWatcher/releases/latest", ct);

            // 404 = repo exists but has no releases yet → treat as up-to-date
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateInfo { CurrentVersion = current, LatestVersion = current };

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var tagName  = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            var notes    = doc.RootElement.TryGetProperty("body", out var bodyEl)
                           ? bodyEl.GetString() ?? string.Empty : string.Empty;

            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var latest)) return null;

            string downloadUrl = string.Empty;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var u)
                                      ? u.GetString() ?? string.Empty : string.Empty;
                        break;
                    }
                }
            }

            return new UpdateInfo
            {
                CurrentVersion = current,
                LatestVersion  = latest,
                TagName        = tagName,
                DownloadUrl    = downloadUrl,
                ReleaseNotes   = notes
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task ApplyUpdateAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default)
    {
        var tempDir      = Path.Combine(Path.GetTempPath(), "WildlifeWatcher-update");
        var zipPath      = Path.Combine(tempDir, "WildlifeWatcher-update.zip");
        var extractDir   = Path.Combine(tempDir, "extracted");
        var scriptPath   = Path.Combine(Path.GetTempPath(), "WildlifeWatcher-updater.ps1");
        var currentExe   = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

        Directory.CreateDirectory(tempDir);
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);

        // Download with progress
        var http = _httpFactory.CreateClient("github");
        using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total    = response.Content.Headers.ContentLength ?? 0L;
        var received = 0L;

        await using (var fs = File.Create(zipPath))
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0)
                    progress.Report((int)(received * 100 / total));
            }
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir);
        progress.Report(100);

        // Find the exe in the extracted folder
        var extractedExe = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                                    .FirstOrDefault() ?? Path.Combine(extractDir, "WildlifeWatcher.exe");

        // Write PS1 updater script
        var script = $"""
            Start-Sleep -Seconds 3
            Copy-Item -Path "{extractedExe}" -Destination "{currentExe}" -Force
            Start-Process "{currentExe}"
            """;
        await File.WriteAllTextAsync(scriptPath, script, ct);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true
        });

        System.Windows.Application.Current.Dispatcher.Invoke(
            System.Windows.Application.Current.Shutdown);
    }
}
