using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class BirdPhotoService : IBirdPhotoService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BirdPhotoService> _logger;
    private readonly string _cacheDir;

    private const int MaxRetries      = 10;
    private const int RetryDelaySecs  = 10;

    public BirdPhotoService(IHttpClientFactory httpFactory, ILogger<BirdPhotoService> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "species_photos");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<string?> FetchAndCachePhotoAsync(string scientificName, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scientificName)) return null;

        var safeName  = string.Concat(scientificName.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var cachePath = Path.Combine(_cacheDir, $"{safeName}.jpg");

        if (!forceRefresh && File.Exists(cachePath)) return cachePath;

        try
        {
            var http = _httpFactory.CreateClient("inaturalist");
            var url  = $"v1/taxa?q={Uri.EscapeDataString(scientificName)}&rank=species&per_page=5";

            string? response = await GetWithRetryAsync(http, url, ct);
            if (response is null) return null;

            using var doc = JsonDocument.Parse(response);
            var results   = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;

            // Try each taxon result until we download a photo successfully
            for (int i = 0; i < results.GetArrayLength(); i++)
            {
                var taxon = results[i];
                if (!taxon.TryGetProperty("default_photo", out var defaultPhoto)) continue;
                if (!defaultPhoto.TryGetProperty("medium_url", out var urlProp)) continue;

                var photoUrl = urlProp.GetString();
                if (string.IsNullOrEmpty(photoUrl)) continue;

                var imageBytes = await GetBytesWithRetryAsync(http, photoUrl, ct);
                if (imageBytes is null) continue;

                if (forceRefresh && File.Exists(cachePath))
                    File.Delete(cachePath);

                await File.WriteAllBytesAsync(cachePath, imageBytes, ct);
                _logger.LogInformation("Cached reference photo for {Name} at {Path}", scientificName, cachePath);
                return cachePath;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch reference photo for {Name}", scientificName);
            return null;
        }
    }

    private async Task<string?> GetWithRetryAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    int delaySecs = GetRetryAfter(resp) ?? RetryDelaySecs;
                    _logger.LogWarning("iNaturalist rate-limited (attempt {A}/{M}), retrying in {D}s", attempt + 1, MaxRetries, delaySecs);
                    await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogWarning(ex, "iNaturalist request failed (attempt {A}/{M}), retrying", attempt + 1, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySecs), ct);
            }
        }
        return null;
    }

    private async Task<byte[]?> GetBytesWithRetryAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var resp = await http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    int delaySecs = GetRetryAfter(resp) ?? RetryDelaySecs;
                    _logger.LogWarning("iNaturalist rate-limited on photo download (attempt {A}/{M}), retrying in {D}s", attempt + 1, MaxRetries, delaySecs);
                    await Task.Delay(TimeSpan.FromSeconds(delaySecs), ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                _logger.LogWarning(ex, "Photo download failed (attempt {A}/{M}), retrying", attempt + 1, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaySecs), ct);
            }
        }
        return null;
    }

    private static int? GetRetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return (int)Math.Ceiling(delta.TotalSeconds);
        return null;
    }
}
