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

    private const int MaxRetries     = 10;
    private const int RetryDelaySecs = 10;

    public BirdPhotoService(IHttpClientFactory httpFactory, ILogger<BirdPhotoService> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", "species_photos");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<(string? Path, bool FetchedFromNetwork, bool RateLimited)> FetchAndCachePhotoAsync(
        string scientificName, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scientificName)) return (null, false, false);

        var safeName  = string.Concat(scientificName.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var cachePath = System.IO.Path.Combine(_cacheDir, $"{safeName}.jpg");

        if (!forceRefresh && File.Exists(cachePath)) return (cachePath, false, false);

        // Stage 1: Wikipedia REST API — curated, no API key, 200 req/sec
        string? photoUrl = await TryGetWikipediaPhotoUrlAsync(scientificName, ct);
        if (photoUrl != null)
            _logger.LogInformation("Using Wikipedia photo for {Name}", scientificName);

        // Stage 2: iNaturalist Taxa API fallback — fail fast on 429
        if (photoUrl is null)
        {
            var (inatUrl, rateLimited) = await TryGetINaturalistTaxaPhotoUrlAsync(scientificName, ct);
            if (rateLimited) return (null, true, true);
            photoUrl = inatUrl;
            if (photoUrl != null)
                _logger.LogInformation("Using iNaturalist taxa photo for {Name}", scientificName);
        }

        if (photoUrl is null) return (null, true, false);

        var http       = _httpFactory.CreateClient("inaturalist"); // absolute URLs work on any named client
        var imageBytes = await GetBytesWithRetryAsync(http, photoUrl, ct);
        if (imageBytes is null) return (null, true, false);

        if (forceRefresh && File.Exists(cachePath))
            File.Delete(cachePath);

        await File.WriteAllBytesAsync(cachePath, imageBytes, ct);
        _logger.LogInformation("Cached reference photo for {Name} at {Path}", scientificName, cachePath);
        return (cachePath, true, false);
    }

    private async Task<(string? Url, bool RateLimited)> TryGetINaturalistTaxaPhotoUrlAsync(string scientificName, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("inaturalist");
            var url  = $"v1/taxa?q={Uri.EscapeDataString(scientificName)}&rank=species&per_page=5";

            var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("iNaturalist rate-limited for {Name}, queuing for retry", scientificName);
                return (null, true);
            }
            if (!resp.IsSuccessStatusCode) return (null, false);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var results = doc.RootElement.GetProperty("results");

            for (int i = 0; i < results.GetArrayLength(); i++)
            {
                var taxon = results[i];
                if (!taxon.TryGetProperty("default_photo", out var defaultPhoto)) continue;
                if (!defaultPhoto.TryGetProperty("medium_url", out var urlProp)) continue;

                var photoUrl = urlProp.GetString();
                if (!string.IsNullOrEmpty(photoUrl)) return (photoUrl, false);
            }

            return (null, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "iNaturalist taxa lookup failed for {Name}", scientificName);
            return (null, false);
        }
    }

    private async Task<string?> TryGetWikipediaPhotoUrlAsync(string scientificName, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient("wikipedia");
            var resp = await http.GetAsync($"page/summary/{Uri.EscapeDataString(scientificName)}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("originalimage", out var orig) &&
                orig.TryGetProperty("source", out var src) &&
                !string.IsNullOrEmpty(src.GetString()))
                return src.GetString();

            if (root.TryGetProperty("thumbnail", out var thumb) &&
                thumb.TryGetProperty("source", out var tsrc) &&
                !string.IsNullOrEmpty(tsrc.GetString()))
                return tsrc.GetString();

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wikipedia photo lookup failed for {Name}", scientificName);
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
