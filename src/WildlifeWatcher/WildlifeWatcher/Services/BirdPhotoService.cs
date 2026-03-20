using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class BirdPhotoService : IBirdPhotoService
{
    private readonly HttpClient _http;
    private readonly ILogger<BirdPhotoService> _logger;
    private readonly string _cacheDir;

    public BirdPhotoService(HttpClient http, ILogger<BirdPhotoService> logger)
    {
        _http     = http;
        _logger   = logger;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "species_photos");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string?> FetchAndCachePhotoAsync(string scientificName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scientificName)) return null;

        var safeName  = string.Concat(scientificName.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var cachePath = Path.Combine(_cacheDir, $"{safeName}.jpg");

        if (File.Exists(cachePath)) return cachePath;

        try
        {
            var url      = $"https://api.inaturalist.org/v1/taxa?q={Uri.EscapeDataString(scientificName)}&rank=species&per_page=1";
            var response = await _http.GetStringAsync(url, ct);

            using var doc    = JsonDocument.Parse(response);
            var results      = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            var photoUrl = results[0]
                .GetProperty("default_photo")
                .GetProperty("medium_url")
                .GetString();

            if (string.IsNullOrEmpty(photoUrl)) return null;

            var imageBytes = await _http.GetByteArrayAsync(photoUrl, ct);
            await File.WriteAllBytesAsync(cachePath, imageBytes, ct);
            _logger.LogInformation("Cached reference photo for {Name} at {Path}", scientificName, cachePath);
            return cachePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch reference photo for {Name}", scientificName);
            return null;
        }
    }
}
