using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Data;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class DataPortService : IDataPortService
{
    private readonly ISettingsService _settingsService;
    private readonly IDbContextFactory<WildlifeDbContext> _dbFactory;
    private readonly ILogger<DataPortService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WildlifeWatcher");

    public DataPortService(
        ISettingsService settingsService,
        IDbContextFactory<WildlifeDbContext> dbFactory,
        ILogger<DataPortService> logger)
    {
        _settingsService = settingsService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task ExportAsync(string zipPath, IProgress<(int percent, string message)> progress, CancellationToken ct)
    {
        progress.Report((0, "Preparing export…"));

        var settings = _settingsService.CurrentSettings;
        var dbPath = settings.GetEffectiveDatabasePath();
        var capturesDir = CaptureStorageService.ResolveCapturesDir(settings.CapturesDirectory);
        var speciesPhotosDir = Path.Combine(AppDataDir, "species_photos");

        // Checkpoint WAL so the database file is self-contained
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", ct);
        }

        // Serialize settings now (on UI thread) so it's safe to access inside Task.Run
        var settingsJson = JsonSerializer.Serialize(settings, JsonOpts);

        // Run all zip I/O on a background thread to keep the UI responsive
        await Task.Run(() =>
        {
            var captureFiles = Directory.Exists(capturesDir)
                ? Directory.GetFiles(capturesDir, "*.jpg")
                : Array.Empty<string>();
            var speciesFiles = Directory.Exists(speciesPhotosDir)
                ? Directory.GetFiles(speciesPhotosDir, "*.jpg")
                : Array.Empty<string>();

            int totalFiles = Math.Max(1, 3 + captureFiles.Length + speciesFiles.Length);
            int done = 0;

            var tempDb = Path.Combine(Path.GetTempPath(), $"ww_export_{Guid.NewGuid():N}.db");
            try
            {
                File.Copy(dbPath, tempDb, overwrite: true);

                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

                // manifest.json
                var manifest = new ExportManifest
                {
                    AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0",
                    ExportedAt = DateTime.UtcNow,
                    OriginalBasePath = AppDataDir
                };
                var manifestEntry = zip.CreateEntry("manifest.json");
                using (var stream = manifestEntry.Open())
                    JsonSerializer.Serialize(stream, manifest, JsonOpts);
                done++;
                progress.Report((done * 100 / totalFiles, "Wrote manifest.json"));

                // settings.json — strip camera URL (not portable between machines)
                var settingsNode = JsonNode.Parse(settingsJson)!;
                settingsNode["RtspUrl"] = string.Empty;
                var sanitizedSettingsEntry = zip.CreateEntry("settings.json");
                using (var sw = new StreamWriter(sanitizedSettingsEntry.Open()))
                    sw.Write(settingsNode.ToJsonString(JsonOpts));
                done++;
                progress.Report((done * 100 / totalFiles, "Wrote settings.json"));

                // wildlife.db
                zip.CreateEntryFromFile(tempDb, "wildlife.db");
                done++;
                progress.Report((done * 100 / totalFiles, "Wrote wildlife.db"));

                // captures/
                foreach (var file in captureFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, $"captures/{Path.GetFileName(file)}");
                    done++;
                    progress.Report((done * 100 / totalFiles, $"Captures: {Path.GetFileName(file)}"));
                }

                // species_photos/
                foreach (var file in speciesFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(file, $"species_photos/{Path.GetFileName(file)}");
                    done++;
                    progress.Report((done * 100 / totalFiles, $"Species photos: {Path.GetFileName(file)}"));
                }

                progress.Report((100, "Export complete."));
                _logger.LogInformation("Exported data to {Path} ({Captures} captures, {Species} species photos)",
                    zipPath, captureFiles.Length, speciesFiles.Length);
            }
            finally
            {
                try { File.Delete(tempDb); } catch { /* best effort */ }
            }
        }, ct);
    }

    public async Task ImportAsync(string zipPath, string preserveRtspUrl, IProgress<(int percent, string message)> progress, CancellationToken ct)
    {
        progress.Report((0, "Validating archive…"));

        var settings = _settingsService.CurrentSettings;
        var capturesDir = CaptureStorageService.ResolveCapturesDir(settings.CapturesDirectory);
        var speciesPhotosDir = Path.Combine(AppDataDir, "species_photos");
        var dbPath = settings.GetEffectiveDatabasePath();
        var currentBasePath = AppDataDir;
        var settingsDestPath = Path.Combine(AppDataDir, "settings.json");

        // Run all zip I/O on a background thread to keep the UI responsive
        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);

            // Validate required entries
            var manifestEntry = zip.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("Invalid archive: missing manifest.json");
            var settingsEntry = zip.GetEntry("settings.json")
                ?? throw new InvalidOperationException("Invalid archive: missing settings.json");
            var dbEntry = zip.GetEntry("wildlife.db")
                ?? throw new InvalidOperationException("Invalid archive: missing wildlife.db");

            // Read manifest
            ExportManifest manifest;
            using (var stream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<ExportManifest>(stream)
                    ?? throw new InvalidOperationException("Failed to read manifest.json");
            }

            var captureEntries = zip.Entries.Where(e => e.FullName.StartsWith("captures/") && e.Name.Length > 0).ToList();
            var speciesEntries = zip.Entries.Where(e => e.FullName.StartsWith("species_photos/") && e.Name.Length > 0).ToList();
            int totalSteps = Math.Max(1, 3 + captureEntries.Count + speciesEntries.Count + 1);
            int done = 0;

            // Extract settings.json
            progress.Report((0, "Restoring settings…"));
            Directory.CreateDirectory(AppDataDir);
            settingsEntry.ExtractToFile(settingsDestPath, overwrite: true);
            done++;
            progress.Report((done * 100 / totalSteps, "Restored settings.json"));

            // Extract wildlife.db via temp file — can't overwrite an open DB directly
            progress.Report((done * 100 / totalSteps, "Restoring database…"));
            var tempDb = Path.Combine(Path.GetTempPath(), $"ww_import_{Guid.NewGuid():N}.db");
            try
            {
                dbEntry.ExtractToFile(tempDb, overwrite: true);
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                 File.Move(tempDb, dbPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tempDb); } catch { /* best effort */ }
            }
            done++;
            progress.Report((done * 100 / totalSteps, "Restored wildlife.db"));

            // Extract captures
            Directory.CreateDirectory(capturesDir);
            foreach (var entry in captureEntries)
            {
                ct.ThrowIfCancellationRequested();
                entry.ExtractToFile(Path.Combine(capturesDir, entry.Name), overwrite: true);
                done++;
                progress.Report((done * 100 / totalSteps, $"Captures: {entry.Name}"));
            }

            // Extract species_photos
            Directory.CreateDirectory(speciesPhotosDir);
            foreach (var entry in speciesEntries)
            {
                ct.ThrowIfCancellationRequested();
                entry.ExtractToFile(Path.Combine(speciesPhotosDir, entry.Name), overwrite: true);
                done++;
                progress.Report((done * 100 / totalSteps, $"Species photos: {entry.Name}"));
            }

            // Always patch settings.json:
            //   - restore camera URL from the importing machine (not from the archive)
            //   - reset CapturesDirectory if it was an absolute path under the old base
            var importedJson = File.ReadAllText(settingsDestPath);
            var importedSettings = JsonSerializer.Deserialize<AppConfiguration>(importedJson) ?? new AppConfiguration();
            importedSettings.RtspUrl = preserveRtspUrl;
            var originalBasePath = manifest.OriginalBasePath;
            if (Path.IsPathRooted(importedSettings.CapturesDirectory) &&
                importedSettings.CapturesDirectory.StartsWith(originalBasePath, StringComparison.OrdinalIgnoreCase))
            {
                importedSettings.CapturesDirectory = "captures";
            }
            File.WriteAllText(settingsDestPath, JsonSerializer.Serialize(importedSettings, JsonOpts));

            // DB path rewriting — only needed when base path differs (cross-machine import)
            if (!string.Equals(originalBasePath, currentBasePath, StringComparison.OrdinalIgnoreCase))
            {
                progress.Report((done * 100 / totalSteps, "Rewriting paths…"));

                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                var escapedOld = originalBasePath.Replace("'", "''");
                var escapedNew = currentBasePath.Replace("'", "''");

                var sql1 = $"UPDATE CaptureRecords SET ImageFilePath = REPLACE(ImageFilePath, '{escapedOld}', '{escapedNew}') WHERE ImageFilePath LIKE '{escapedOld}%'";
                var sql2 = $"UPDATE CaptureRecords SET AnnotatedImageFilePath = REPLACE(AnnotatedImageFilePath, '{escapedOld}', '{escapedNew}') WHERE AnnotatedImageFilePath LIKE '{escapedOld}%'";
                var sql3 = $"UPDATE Species SET ReferencePhotoPath = REPLACE(ReferencePhotoPath, '{escapedOld}', '{escapedNew}') WHERE ReferencePhotoPath LIKE '{escapedOld}%'";

                using (var cmd = new SqliteCommand(sql1, conn)) { cmd.ExecuteNonQuery(); }
                using (var cmd = new SqliteCommand(sql2, conn)) { cmd.ExecuteNonQuery(); }
                using (var cmd = new SqliteCommand(sql3, conn)) { cmd.ExecuteNonQuery(); }

                _logger.LogInformation("Rewrote paths from {Old} to {New}", originalBasePath, currentBasePath);
            }

            done++;
            progress.Report((100, "Import complete."));
            _logger.LogInformation("Imported data from {Path}", zipPath);
        }, ct);
    }
}
