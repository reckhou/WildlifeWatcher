using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface ISunriseSunsetService
{
    /// <summary>
    /// Returns true if the current local time falls within the detection window.
    /// Window = [sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes].
    /// Always returns true when EnableDaylightDetectionOnly is false.
    /// Uses 06:00–20:00 local fallback when no location is configured.
    /// Thread-safe: reads from in-memory cache only — no I/O performed here.
    /// </summary>
    bool IsDetectionAllowed(AppConfiguration settings);

    /// <summary>True when no location is set and the 06:00–20:00 fallback is in use.</summary>
    bool IsUsingFallback { get; }

    /// <summary>
    /// Next window transition time for status display.
    /// If currently inside window: time detection will pause (windowEnd).
    /// If currently outside window: time detection will resume (next windowStart).
    /// </summary>
    DateTime NextTransitionTime { get; }

    /// <summary>
    /// Refresh today's sunrise/sunset from the weather service if the cached date
    /// is stale (i.e. the calendar date has changed). No-op if cache is current.
    /// Must catch all exceptions internally and log them — caller discards the Task.
    /// </summary>
    Task RefreshIfNeededAsync(AppConfiguration settings);
}
