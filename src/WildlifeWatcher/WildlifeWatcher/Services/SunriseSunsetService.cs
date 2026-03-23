using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class SunriseSunsetService : ISunriseSunsetService
{
    private readonly IWeatherService                 _weather;
    private readonly ILogger<SunriseSunsetService>   _logger;

    // Cache — refreshed once per calendar day
    private DateOnly  _cachedDate    = DateOnly.MinValue;
    private DateTime  _cachedSunrise = DateTime.MinValue;
    private DateTime  _cachedSunset  = DateTime.MinValue;

    public bool     IsUsingFallback    { get; private set; }
    public DateTime NextTransitionTime { get; private set; }

    public SunriseSunsetService(IWeatherService weather, ILogger<SunriseSunsetService> logger)
    {
        _weather = weather;
        _logger  = logger;
    }

    public bool IsDetectionAllowed(AppConfiguration settings)
    {
        if (!settings.EnableDaylightDetectionOnly)
            return true;

        var (windowStart, windowEnd) = GetWindow(settings);
        var now = DateTime.Now;

        bool allowed = IsWithinWindowAt(now, windowStart, windowEnd);
        NextTransitionTime = allowed ? windowEnd : ComputeNextStart(now, windowStart, windowEnd);
        return allowed;
    }

    /// <summary>Internal for unit tests — checks a specific instant against a specific window.</summary>
    internal bool IsWithinWindowAt(DateTime now, DateTime windowStart, DateTime windowEnd)
        => now >= windowStart && now <= windowEnd;

    public async Task RefreshIfNeededAsync(AppConfiguration settings)
    {
        if (DateOnly.FromDateTime(DateTime.Today) == _cachedDate)
            return; // cache is current

        try
        {
            if (settings.Latitude is null || settings.Longitude is null)
            {
                UseFallback();
                return;
            }

            var snapshot = await _weather.GetCurrentWeatherAsync(
                settings.Latitude.Value, settings.Longitude.Value);

            if (snapshot?.Sunrise is null || snapshot.Sunset is null)
            {
                _logger.LogWarning("Weather response missing sunrise/sunset — using fallback window");
                if (_cachedDate == DateOnly.MinValue)
                    UseFallback();
                else
                    _cachedDate = DateOnly.FromDateTime(DateTime.Today); // keep yesterday's times but suppress re-fetch today
                return;
            }

            _cachedDate    = DateOnly.FromDateTime(DateTime.Today);
            _cachedSunrise = snapshot.Sunrise.Value;
            _cachedSunset  = snapshot.Sunset.Value;
            IsUsingFallback = false;

            _logger.LogInformation(
                "Daylight window refreshed: sunrise={Sunrise:HH:mm}, sunset={Sunset:HH:mm}",
                _cachedSunrise, _cachedSunset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh sunrise/sunset — using {Fallback}",
                _cachedDate == DateOnly.MinValue ? "06:00–20:00 fallback" : "yesterday's cache");

            if (_cachedDate == DateOnly.MinValue)
                UseFallback();
            else
                _cachedDate = DateOnly.FromDateTime(DateTime.Today); // suppress re-fetch today, reuse yesterday's times
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void UseFallback()
    {
        _cachedDate     = DateOnly.FromDateTime(DateTime.Today);
        _cachedSunrise  = DateTime.Today.AddHours(6);
        _cachedSunset   = DateTime.Today.AddHours(20);
        IsUsingFallback = true;
    }

    private (DateTime start, DateTime end) GetWindow(AppConfiguration settings)
    {
        // If no cache yet (RefreshIfNeededAsync not called yet), use fallback in-line
        if (_cachedDate == DateOnly.MinValue)
            UseFallback();

        var sunrise = _cachedDate == DateOnly.FromDateTime(DateTime.Today)
            ? _cachedSunrise
            : DateTime.Today.AddHours(6); // stale cache safety net

        var sunset  = _cachedDate == DateOnly.FromDateTime(DateTime.Today)
            ? _cachedSunset
            : DateTime.Today.AddHours(20);

        return (
            sunrise.AddMinutes(settings.SunriseOffsetMinutes),
            sunset.AddMinutes(settings.SunsetOffsetMinutes)
        );
    }

    private static DateTime ComputeNextStart(DateTime now, DateTime windowStart, DateTime windowEnd)
    {
        if (now < windowStart)
            return windowStart;

        // now >= windowEnd: detection window for today has closed — next is tomorrow's start
        return windowStart.AddDays(1);
    }
}
