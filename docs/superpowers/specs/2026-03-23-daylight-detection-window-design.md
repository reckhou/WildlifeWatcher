# Daylight Detection Window — Design Spec

**Date:** 2026-03-23
**Status:** Approved

---

## Overview

Wildlife detection (AI calls) should only run during daylight hours — between a configurable offset before sunrise and a configurable offset after sunset. Background model training (EMA) continues unconditionally regardless of the daylight window. The feature is opt-in via a toggle.

---

## Requirements

1. **Toggle** — `EnableDaylightDetectionOnly` (default: `false`). When off, detection runs 24/7 as before.
2. **Offsets** — Two separate integer settings using a consistent sign convention for both:
   - Positive value = later than the base time (e.g. `+30` = 30 min after)
   - Negative value = earlier than the base time (e.g. `-30` = 30 min before)
   - `SunriseOffsetMinutes` (default: `-30`) — when detection starts relative to sunrise
   - `SunsetOffsetMinutes` (default: `30`) — when detection ends relative to sunset
   - Detection window: `[sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes]`
3. **Sunrise/sunset source** — fetched from Open-Meteo via existing `IWeatherService`, using configured `Latitude`/`Longitude`.
4. **Fallback when no location set** — use local `06:00–20:00`. Show a warning notification to set location for accurate times.
5. **Fallback when fetch fails** — reuse yesterday's cached times. Fall back to `06:00–20:00` only if no cache is available at all.
6. **Background learning** — EMA background model updates every tick regardless of daylight window.
7. **Status display** — when paused due to daylight window, show: *"Detection paused — outside daylight window (next: HH:mm)"*

---

## Architecture

### New: `ISunriseSunsetService`

```csharp
public interface ISunriseSunsetService
{
    /// <summary>Returns true if the current local time falls within the detection window.</summary>
    bool IsDetectionAllowed(AppConfiguration settings);

    /// <summary>True when no location is set and the 06:00–20:00 fallback is in use.</summary>
    bool IsUsingFallback { get; }

    /// <summary>Next transition time (start or end of detection window) for status display.</summary>
    DateTime NextTransitionTime { get; }
}
```

**Implementation (`SunriseSunsetService`):**

The service pre-fetches and caches sunrise/sunset on a daily basis — it does **not** perform I/O inside `IsDetectionAllowed`. The synchronous `IsDetectionAllowed` only reads from in-memory cache, so there is no blocking on the detection loop thread.

Daily refresh strategy:
- On startup: call `RefreshAsync()` once immediately
- Use a background timer (or check in `RefreshIfNeededAsync` called from the loop) that triggers once per day — specifically when `DateTime.Today` changes from the last cached date
- Refresh is `async` and isolated from the synchronous `IsDetectionAllowed` path
- **`RefreshAsync` must catch all exceptions internally and log them.** The method is called fire-and-forget from the tick loop; an unhandled exception would be silently swallowed by .NET. All exception handling must be inside `RefreshAsync` itself, not at the call site.

On each `RefreshAsync()`:
1. If no location configured (`Latitude == null || Longitude == null`): set `IsUsingFallback = true`, use `06:00–20:00`
2. Otherwise call `IWeatherService.GetCurrentWeatherAsync(lat, lon)`:
   - On success: update cache with new `(date, sunrise, sunset)`; set `IsUsingFallback = false`
   - On failure: reuse yesterday's cached times; if no cache, fall back to `06:00–20:00` and set `IsUsingFallback = true`

**DateTime kind fix:** Open-Meteo returns sunrise/sunset as local-time strings (e.g. `"2026-03-23T06:12"`) because the request uses `timezone=auto`. `DateTime.Parse` produces `Kind = Unspecified`. The implementation must call `DateTime.SpecifyKind(parsed, DateTimeKind.Local)` when storing sunrise/sunset to ensure correct comparison against `DateTime.Now` (Local). This fix must also be applied to `OpenMeteoWeatherService` itself (pre-existing bug).

`IsDetectionAllowed` logic:
```
windowStart = sunrise + SunriseOffsetMinutes
windowEnd   = sunset  + SunsetOffsetMinutes
return DateTime.Now >= windowStart && DateTime.Now <= windowEnd
```

`NextTransitionTime`:
- If currently inside window → returns `windowEnd`
- If currently outside window → returns next day's `windowStart` if `DateTime.Now >= windowEnd`, else today's `windowStart`

Registered as a **singleton** in DI.

---

### Modified: `IRecognitionLoopService`

Add a new event for the UI to subscribe to:

```csharp
/// <summary>Fired when detection is paused or resumed due to the daylight window gate.</summary>
event EventHandler<bool>? DaylightWindowChanged;  // true = detection allowed, false = paused
```

---

### Modified: `RecognitionLoopService`

Inject `ISunriseSunsetService`. Add a new gate in `ProcessTickAsync` **after** the training gate, **before** POI extraction. Also trigger the daily async refresh when the date changes (fire-and-forget, non-blocking):

```
tick
  → extract frame
  → update EMA background model        ← always runs
  → [gate: training complete?]
  → trigger async refresh if date changed (fire-and-forget)
  → [gate: daylight window?]           ← NEW
  → extract POI crops
  → [gate: POI count > 0?]
  → [gate: cooldown expired?]
  → send to AI
  → filter by confidence
  → save capture + fire event
```

When the daylight gate blocks:
- Log: `"Outside daylight window — detection paused until {NextTransition:HH:mm}"`
- Fire `DaylightWindowChanged(false)` event (only when state changes, not every tick)

When detection resumes after being paused:
- Fire `DaylightWindowChanged(true)`

---

### Modified: `AppConfiguration`

Three new properties:

```csharp
/// <summary>
/// When true, AI detection only runs within the daylight window
/// [sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes].
/// Background model training continues unconditionally.
/// </summary>
public bool EnableDaylightDetectionOnly { get; set; } = false;

/// <summary>
/// Minutes offset applied to sunrise. Negative = before sunrise, positive = after.
/// Default: -30 (start detecting 30 minutes before sunrise).
/// </summary>
public int SunriseOffsetMinutes { get; set; } = -30;

/// <summary>
/// Minutes offset applied to sunset. Positive = after sunset, negative = before.
/// Default: 30 (stop detecting 30 minutes after sunset).
/// </summary>
public int SunsetOffsetMinutes { get; set; } = 30;
```

---

### Modified: Settings UI

- New **Daylight Detection** section in the settings panel, near the existing location fields
- Toggle for `EnableDaylightDetectionOnly`
- Two numeric inputs for `SunriseOffsetMinutes` and `SunsetOffsetMinutes` (enabled only when toggle is on)
- Inline warning when toggle is on but no location is set: *"No location set — using 06:00–20:00 fallback. Set a location for accurate sunrise/sunset times."*

### Modified: Live View Status

When detection is paused by the daylight gate, show in the status area:
> *"Detection paused — outside daylight window (next: HH:mm)"*

---

## Error Handling

| Scenario | Behaviour |
|---|---|
| No location configured | Use 06:00–20:00 fallback; show location warning |
| Weather API fetch fails | Reuse yesterday's cached sunrise/sunset |
| No cache and fetch fails | Use 06:00–20:00 fallback |
| Feature toggled off | Bypass gate entirely — 24/7 detection as before |

---

## Known Limitations

- **Midnight-spanning windows (polar latitudes):** At high latitudes in summer, `sunset + SunsetOffsetMinutes` may push past midnight, producing a window where `windowEnd < windowStart`. This edge case is out of scope; the gate will silently always-block in this scenario. A future improvement could invert the comparison for wrapping windows.
- **DST transition days:** The service detects date changes via `DateTime.Today`. On DST transition days the cached sunrise/sunset remain valid (Open-Meteo uses `timezone=auto` local times), but the cache will not be proactively refreshed until the next date boundary. This is acceptable — the error is at most ~1 hour on two days per year.

---

## Out of Scope

- Per-day-of-week schedules
- Multiple detection windows per day
- Push notifications for window transitions
- Polar midnight-sun / polar-night handling
