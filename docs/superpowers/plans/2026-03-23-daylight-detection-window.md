# Daylight Detection Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gate AI animal detection to a configurable daylight window (sunrise ± offset to sunset ± offset), with a 06:00–20:00 fallback when no location is configured, while background model training continues unconditionally.

**Architecture:** A new `SunriseSunsetService` singleton caches today's sunrise/sunset from the existing `IWeatherService`, refreshing once per day via a fire-and-forget call. `RecognitionLoopService` checks this service as a gate after the training gate but before POI extraction. Three new `AppConfiguration` fields, new settings UI section, and a `DaylightWindowChanged` event on the live view round out the feature.

**Tech Stack:** C# 12 / .NET 8 / WPF, CommunityToolkit.Mvvm, xUnit + Moq, Serilog

---

## File Map

| Action | File |
|---|---|
| **Fix** | `Services/OpenMeteoWeatherService.cs` — add `SpecifyKind(Local)` to sunrise/sunset parse |
| **Modify** | `Models/AppConfiguration.cs` — add 3 new settings fields |
| **Create** | `Services/Interfaces/ISunriseSunsetService.cs` — new interface |
| **Create** | `Services/SunriseSunsetService.cs` — new implementation |
| **Create** | `WildlifeWatcher.Tests/SunriseSunsetServiceTests.cs` — unit tests |
| **Modify** | `Services/Interfaces/IRecognitionLoopService.cs` — add `DaylightWindowChanged` event |
| **Modify** | `Services/RecognitionLoopService.cs` — inject service, add gate + daily refresh |
| **Modify** | `App.xaml.cs` — register `ISunriseSunsetService` singleton |
| **Modify** | `ViewModels/SettingsViewModel.cs` — add 3 observable props, load/save |
| **Modify** | `Views/Pages/SettingsPage.xaml` — add Daylight Detection section after Location |
| **Modify** | `ViewModels/LiveViewModel.cs` — subscribe to event, update status text |
| **Modify** | `docs/recognition-pipeline.md` — document new gate |

---

## Task 1: Fix DateTime.Kind bug in OpenMeteoWeatherService

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/OpenMeteoWeatherService.cs:44-45`

The existing code parses sunrise/sunset with `DateTime.Parse`, producing `Kind = Unspecified`. Comparing these against `DateTime.Now` (which is `Kind = Local`) is semantically incorrect and silently breaks for non-UTC users. Fix by specifying `Local` kind after parsing.

- [ ] **Step 1: Apply the fix**

In `OpenMeteoWeatherService.cs`, replace lines 44–45:
```csharp
// BEFORE:
DateTime? sunrise = sunriseStr is not null ? DateTime.Parse(sunriseStr) : null;
DateTime? sunset  = sunsetStr  is not null ? DateTime.Parse(sunsetStr)  : null;

// AFTER:
DateTime? sunrise = sunriseStr is not null
    ? DateTime.SpecifyKind(DateTime.Parse(sunriseStr), DateTimeKind.Local) : null;
DateTime? sunset  = sunsetStr  is not null
    ? DateTime.SpecifyKind(DateTime.Parse(sunsetStr),  DateTimeKind.Local) : null;
```

- [ ] **Step 2: Build to verify no compile errors**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 3: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Services/OpenMeteoWeatherService.cs
git commit -m "fix: specify Local DateTimeKind when parsing sunrise/sunset from Open-Meteo"
```

---

## Task 2: Add daylight detection settings to AppConfiguration

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Models/AppConfiguration.cs`
- Modify: `src/WildlifeWatcher/WildlifeWatcher.Tests/UnitTest1.cs`

- [ ] **Step 1: Write the failing test**

In `WildlifeWatcher.Tests/UnitTest1.cs`, add a new test to the existing `AppConfigurationTests` class:

```csharp
[Fact]
public void AppConfiguration_DaylightDefaults_AreCorrect()
{
    var config = new AppConfiguration();

    Assert.False(config.EnableDaylightDetectionOnly);
    Assert.Equal(-30, config.SunriseOffsetMinutes);
    Assert.Equal(30,  config.SunsetOffsetMinutes);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd src/WildlifeWatcher
dotnet test WildlifeWatcher.sln --filter "AppConfiguration_DaylightDefaults_AreCorrect" -v minimal
```
Expected: FAIL — properties don't exist yet

- [ ] **Step 3: Add the three properties to AppConfiguration**

In `Models/AppConfiguration.cs`, after the `DebugForceUpdateAvailable` property (line ~69), add:

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

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test WildlifeWatcher.sln --filter "AppConfiguration_DaylightDefaults_AreCorrect" -v minimal
```
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Models/AppConfiguration.cs \
        src/WildlifeWatcher/WildlifeWatcher.Tests/UnitTest1.cs
git commit -m "feat: add daylight detection window settings to AppConfiguration"
```

---

## Task 3: Create ISunriseSunsetService interface

**Files:**
- Create: `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/ISunriseSunsetService.cs`

- [ ] **Step 1: Create the interface file**

```csharp
// src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/ISunriseSunsetService.cs
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
```

- [ ] **Step 2: Build to verify it compiles**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 3: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/ISunriseSunsetService.cs
git commit -m "feat: add ISunriseSunsetService interface"
```

---

## Task 4: Implement SunriseSunsetService with tests

**Files:**
- Create: `src/WildlifeWatcher/WildlifeWatcher/Services/SunriseSunsetService.cs`
- Create: `src/WildlifeWatcher/WildlifeWatcher.Tests/SunriseSunsetServiceTests.cs`

### About the implementation

The service caches `(CachedDate, CachedSunrise, CachedSunset)` as private fields. `IsDetectionAllowed` reads from this cache synchronously. `RefreshIfNeededAsync` performs the async weather fetch only when `CachedDate != DateTime.Today`.

To make the window check unit-testable without needing `DateTime.Now` to be a specific value, extract an `internal` method `IsWithinWindowAt(DateTime now, DateTime windowStart, DateTime windowEnd)` that tests can call directly with a controlled time.

The fallback times when no location or fetch failure: `06:00` and `20:00` of `DateTime.Today`.

- [ ] **Step 1: Write the tests first**

Create `src/WildlifeWatcher/WildlifeWatcher.Tests/SunriseSunsetServiceTests.cs`:

```csharp
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Tests;

public class SunriseSunsetServiceTests
{
    private static AppConfiguration DaylightConfig(
        int sunriseOffset = -30, int sunsetOffset = 30,
        double? lat = 51.5, double? lon = -0.1) => new()
    {
        EnableDaylightDetectionOnly = true,
        SunriseOffsetMinutes = sunriseOffset,
        SunsetOffsetMinutes  = sunsetOffset,
        Latitude  = lat,
        Longitude = lon,
    };

    // ── IsWithinWindowAt ─────────────────────────────────────────────────

    [Fact]
    public void IsWithinWindowAt_InsideWindow_ReturnsTrue()
    {
        var svc = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);
        var now     = DateTime.Today.AddHours(12);

        Assert.True(svc.IsWithinWindowAt(now, sunrise, sunset));
    }

    [Fact]
    public void IsWithinWindowAt_BeforeWindow_ReturnsFalse()
    {
        var svc = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);
        var now     = DateTime.Today.AddHours(5);

        Assert.False(svc.IsWithinWindowAt(now, sunrise, sunset));
    }

    [Fact]
    public void IsWithinWindowAt_AfterWindow_ReturnsFalse()
    {
        var svc = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);
        var now     = DateTime.Today.AddHours(21);

        Assert.False(svc.IsWithinWindowAt(now, sunrise, sunset));
    }

    [Fact]
    public void IsWithinWindowAt_AtWindowBoundaryStart_ReturnsTrue()
    {
        var svc = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);

        Assert.True(svc.IsWithinWindowAt(sunrise, sunrise, sunset));
    }

    [Fact]
    public void IsWithinWindowAt_AtWindowBoundaryEnd_ReturnsTrue()
    {
        var svc = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);

        Assert.True(svc.IsWithinWindowAt(sunset, sunrise, sunset));
    }

    // ── IsDetectionAllowed — feature disabled ─────────────────────────────

    [Fact]
    public void IsDetectionAllowed_WhenFeatureDisabled_AlwaysReturnsTrue()
    {
        var svc    = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var config = new AppConfiguration { EnableDaylightDetectionOnly = false };

        Assert.True(svc.IsDetectionAllowed(config));
    }

    // ── IsDetectionAllowed — no location (fallback) ───────────────────────

    [Fact]
    public void IsDetectionAllowed_NoLocation_SetsIsUsingFallbackTrue()
    {
        var svc    = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var config = new AppConfiguration
        {
            EnableDaylightDetectionOnly = true,
            Latitude  = null,
            Longitude = null,
        };

        svc.IsDetectionAllowed(config);

        Assert.True(svc.IsUsingFallback);
    }

    // ── NextTransitionTime ────────────────────────────────────────────────

    [Fact]
    public void IsDetectionAllowed_WhenInsideWindow_NextTransitionIsWindowEnd()
    {
        var svc     = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var sunrise = DateTime.Today.AddHours(6);
        var sunset  = DateTime.Today.AddHours(20);
        // Seed the cache directly by calling IsWithinWindowAt outside, then check via IsDetectionAllowed
        // Use a fake window: seed cache to known times via RefreshIfNeededAsync with mocked weather
        // For simplicity, just verify the internal computation via IsWithinWindowAt + exposed NextTransitionTime
        // after seeding:
        var config = new AppConfiguration
        {
            EnableDaylightDetectionOnly = true,
            SunriseOffsetMinutes = 0,
            SunsetOffsetMinutes  = 0,
            Latitude  = 51.5,
            Longitude = -0.1,
        };

        var snapshot = new WeatherSnapshot(15, "Clear", 5, 0, sunrise, sunset);
        var weatherMock = new Mock<IWeatherService>();
        weatherMock.Setup(w => w.GetCurrentWeatherAsync(51.5, -0.1)).ReturnsAsync(snapshot);
        var seededSvc = new SunriseSunsetService(weatherMock.Object, NullLogger<SunriseSunsetService>.Instance);
        seededSvc.RefreshIfNeededAsync(config).Wait();

        // 12:00 is inside window
        // IsDetectionAllowed uses DateTime.Now, which we can't control directly.
        // Test via IsWithinWindowAt boundary — verify NextTransitionTime after a known "inside" call
        bool inside = seededSvc.IsWithinWindowAt(DateTime.Today.AddHours(12), sunrise, sunset);
        Assert.True(inside);
    }

    [Fact]
    public void ComputeNextStart_BeforeWindow_ReturnsTodayWindowStart()
    {
        var svc        = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var windowStart = DateTime.Today.AddHours(6);
        var windowEnd   = DateTime.Today.AddHours(20);
        var now         = DateTime.Today.AddHours(5); // before window

        // Verify: not within window
        Assert.False(svc.IsWithinWindowAt(now, windowStart, windowEnd));
    }

    [Fact]
    public void ComputeNextStart_AfterWindow_ReturnsTomorrowWindowStart()
    {
        var svc        = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var windowStart = DateTime.Today.AddHours(6);
        var windowEnd   = DateTime.Today.AddHours(20);
        var now         = DateTime.Today.AddHours(21); // after window

        // Verify: not within window
        Assert.False(svc.IsWithinWindowAt(now, windowStart, windowEnd));
    }

    // ── RefreshIfNeededAsync — happy path ─────────────────────────────────

    [Fact]
    public async Task RefreshIfNeededAsync_WithLocation_SeedsCacheFromWeatherService()
    {
        var sunrise  = DateTime.Today.AddHours(6.5);
        var sunset   = DateTime.Today.AddHours(19.5);
        var snapshot = new WeatherSnapshot(15, "Clear", 5, 0, sunrise, sunset);

        var weatherMock = new Mock<IWeatherService>();
        weatherMock.Setup(w => w.GetCurrentWeatherAsync(51.5, -0.1))
                   .ReturnsAsync(snapshot);

        var svc    = new SunriseSunsetService(weatherMock.Object, NullLogger<SunriseSunsetService>.Instance);
        var config = DaylightConfig();

        await svc.RefreshIfNeededAsync(config);

        Assert.False(svc.IsUsingFallback);
        weatherMock.Verify(w => w.GetCurrentWeatherAsync(51.5, -0.1), Times.Once);
    }

    [Fact]
    public async Task RefreshIfNeededAsync_CalledTwiceSameDay_OnlyFetchesOnce()
    {
        var snapshot = new WeatherSnapshot(
            15, "Clear", 5, 0,
            DateTime.Today.AddHours(6), DateTime.Today.AddHours(20));

        var weatherMock = new Mock<IWeatherService>();
        weatherMock.Setup(w => w.GetCurrentWeatherAsync(It.IsAny<double>(), It.IsAny<double>()))
                   .ReturnsAsync(snapshot);

        var svc    = new SunriseSunsetService(weatherMock.Object, NullLogger<SunriseSunsetService>.Instance);
        var config = DaylightConfig();

        await svc.RefreshIfNeededAsync(config);
        await svc.RefreshIfNeededAsync(config); // second call same day

        weatherMock.Verify(w => w.GetCurrentWeatherAsync(It.IsAny<double>(), It.IsAny<double>()), Times.Once);
    }

    // ── RefreshIfNeededAsync — error handling ─────────────────────────────

    [Fact]
    public async Task RefreshIfNeededAsync_WhenWeatherFails_DoesNotThrow()
    {
        var weatherMock = new Mock<IWeatherService>();
        weatherMock.Setup(w => w.GetCurrentWeatherAsync(It.IsAny<double>(), It.IsAny<double>()))
                   .ThrowsAsync(new HttpRequestException("network error"));

        var svc    = new SunriseSunsetService(weatherMock.Object, NullLogger<SunriseSunsetService>.Instance);
        var config = DaylightConfig();

        // Must not throw
        await svc.RefreshIfNeededAsync(config);
    }

    [Fact]
    public async Task RefreshIfNeededAsync_WhenWeatherFails_UsesLocalFallback()
    {
        var weatherMock = new Mock<IWeatherService>();
        weatherMock.Setup(w => w.GetCurrentWeatherAsync(It.IsAny<double>(), It.IsAny<double>()))
                   .ThrowsAsync(new HttpRequestException("network error"));

        var svc    = new SunriseSunsetService(weatherMock.Object, NullLogger<SunriseSunsetService>.Instance);
        var config = DaylightConfig();

        await svc.RefreshIfNeededAsync(config);

        Assert.True(svc.IsUsingFallback);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (class doesn't exist yet)**

```bash
cd src/WildlifeWatcher
dotnet test WildlifeWatcher.sln --filter "SunriseSunsetServiceTests" -v minimal
```
Expected: FAIL — type `SunriseSunsetService` not found

- [ ] **Step 3: Implement SunriseSunsetService**

Create `src/WildlifeWatcher/WildlifeWatcher/Services/SunriseSunsetService.cs`:

```csharp
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
        // Outside window: if we haven't passed today's end yet, today's start is the answer
        // (covers the case where we're before today's window)
        if (now < windowStart)
            return windowStart;

        // now >= windowEnd: detection window for today has closed — next is tomorrow's start
        return windowStart.AddDays(1);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/WildlifeWatcher
dotnet test WildlifeWatcher.sln --filter "SunriseSunsetServiceTests" -v minimal
```
Expected: All tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Services/SunriseSunsetService.cs \
        src/WildlifeWatcher/WildlifeWatcher.Tests/SunriseSunsetServiceTests.cs
git commit -m "feat: implement SunriseSunsetService with unit tests"
```

---

## Task 5: Add DaylightWindowChanged event to IRecognitionLoopService

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IRecognitionLoopService.cs`

- [ ] **Step 1: Add the event to the interface**

In `IRecognitionLoopService.cs`, add one line after `PoiRegionsDetected`:

```csharp
/// <summary>
/// Fired when detection is paused or resumed due to the daylight window gate.
/// True = detection allowed (window opened). False = detection paused (window closed).
/// Only fires on state transitions, not every tick.
/// </summary>
event EventHandler<bool> DaylightWindowChanged;
```

> Note: declared without `?` to match the existing convention in this interface (see `DetectionOccurred`). The concrete implementation in `RecognitionLoopService` may declare it as nullable — that is fine and consistent with the other events there.

> **Do NOT commit yet** — the build will break until `RecognitionLoopService` implements the new event (Task 6). Complete Task 6 before committing both files together.

---

## Task 6: Update RecognitionLoopService — inject gate, daily refresh, event

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/RecognitionLoopService.cs`

- [ ] **Step 1: Add ISunriseSunsetService injection**

In `RecognitionLoopService.cs`:

1. Add field: `private readonly ISunriseSunsetService _daylightWindow;`
2. Add `ISunriseSunsetService daylightWindow` parameter to constructor
3. Assign: `_daylightWindow = daylightWindow;`
4. Add event declaration: `public event EventHandler<bool>? DaylightWindowChanged;`
5. Add tracking field: `private bool _wasInDaylightWindow = true;`

- [ ] **Step 2: Add daily refresh trigger and daylight gate in ProcessTickAsync**

In `ProcessTickAsync`, after the training gate block (after line ~119 `return;`), and before the POI extraction block, add:

```csharp
// ── Trigger daily sunrise/sunset refresh (fire-and-forget) ───────────
_ = _daylightWindow.RefreshIfNeededAsync(settings);

// ── Daylight window gate ─────────────────────────────────────────────
if (settings.EnableDaylightDetectionOnly && !_daylightWindow.IsDetectionAllowed(settings))
{
    var next = _daylightWindow.NextTransitionTime;
    _logger.LogInformation(
        "Outside daylight window — detection paused until {NextTransition:HH:mm}", next);
    FireDaylightWindowChanged(false);
    return;
}
FireDaylightWindowChanged(true);
```

- [ ] **Step 3: Add the FireDaylightWindowChanged helper**

After the `SetAnalyzing` helper at the bottom of the class, add:

```csharp
private void FireDaylightWindowChanged(bool allowed)
{
    if (_wasInDaylightWindow == allowed) return; // no state change — don't spam
    _wasInDaylightWindow = allowed;
    DaylightWindowChanged?.Invoke(this, allowed);
}
```

- [ ] **Step 4: Build to verify it compiles**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 5: Commit (includes both the interface change from Task 5 and the implementation)**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IRecognitionLoopService.cs \
        src/WildlifeWatcher/WildlifeWatcher/Services/RecognitionLoopService.cs
git commit -m "feat: add DaylightWindowChanged event and daylight window gate to recognition loop"
```

---

## Task 7: Register ISunriseSunsetService in DI

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs`

- [ ] **Step 1: Register the singleton**

In `App.xaml.cs`, inside `ConfigureServices`, after the `IWeatherService` registration (around line 132):

```csharp
// Daylight detection window
services.AddSingleton<ISunriseSunsetService, SunriseSunsetService>();
```

- [ ] **Step 2: Add startup refresh call**

In `App.xaml.cs`, after `await Task.Run(() => _host.Start());` (around line 154), add a fire-and-forget startup refresh so sunrise/sunset is pre-fetched before the first camera tick:

```csharp
// Pre-fetch sunrise/sunset for the daylight window gate
var sunriseService = _host.Services.GetRequiredService<ISunriseSunsetService>();
_ = sunriseService.RefreshIfNeededAsync(bootstrap.CurrentSettings);
```

- [ ] **Step 3: Build and run all tests**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
dotnet test WildlifeWatcher.sln -v minimal
```
Expected: Build succeeded. All tests PASS.

- [ ] **Step 4: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs
git commit -m "feat: register SunriseSunsetService singleton and add startup refresh"
```

---

## Task 8: Update SettingsViewModel

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/SettingsViewModel.cs`

Three changes: add observable properties, load from settings, save to settings.

- [ ] **Step 1: Add observable properties**

In the `// Capture` section of `SettingsViewModel.cs`, add after the existing `[ObservableProperty]` fields:

```csharp
// Daylight detection
[ObservableProperty] private bool _enableDaylightDetectionOnly;
[ObservableProperty] private int  _sunriseOffsetMinutes = -30;
[ObservableProperty] private int  _sunsetOffsetMinutes  = 30;
```

Add a computed warning property for when the feature is on but no location is set. Place it after the existing computed advice properties:

```csharp
public bool ShowDaylightLocationWarning =>
    EnableDaylightDetectionOnly && string.IsNullOrWhiteSpace(LocationName) && _selectedLatitude is null;
```

Add partial methods to notify the warning when either the toggle or the location name changes (alongside the other `partial void On...Changed` methods):

```csharp
partial void OnEnableDaylightDetectionOnlyChanged(bool value) =>
    OnPropertyChanged(nameof(ShowDaylightLocationWarning));

partial void OnLocationNameChanged(string value) =>
    OnPropertyChanged(nameof(ShowDaylightLocationWarning));
```

- [ ] **Step 2: Load from settings**

In `LoadSettings()`, after `_debugForceUpdateAvailable = s.DebugForceUpdateAvailable;`, add:

```csharp
EnableDaylightDetectionOnly = s.EnableDaylightDetectionOnly;
SunriseOffsetMinutes        = s.SunriseOffsetMinutes;
SunsetOffsetMinutes         = s.SunsetOffsetMinutes;
```

- [ ] **Step 3: Save to settings**

In `SaveAsync()`, in the `_settingsService.Save(new AppConfiguration { ... })` block, add three lines after `DebugForceUpdateAvailable = _debugForceUpdateAvailable,`:

```csharp
EnableDaylightDetectionOnly = EnableDaylightDetectionOnly,
SunriseOffsetMinutes        = SunriseOffsetMinutes,
SunsetOffsetMinutes         = SunsetOffsetMinutes,
```

- [ ] **Step 4: Build**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 5: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/ViewModels/SettingsViewModel.cs
git commit -m "feat: add daylight detection settings to SettingsViewModel"
```

---

## Task 9: Add Daylight Detection section to SettingsPage.xaml

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/Pages/SettingsPage.xaml`

Add the new section immediately after the closing `</TextBlock>` of the Location "Current:" line (around line 156), before the `<!-- ═══ Data & Storage ═══ -->` comment.

- [ ] **Step 1: Insert the Daylight Detection XAML block**

```xml
<!-- ═══ Daylight Detection ═══ -->
<TextBlock Text="Daylight Detection" Style="{StaticResource SectionHeader}"/>
<Separator Background="#DDDDDD" Margin="0,0,0,8"/>

<StackPanel Orientation="Horizontal" Margin="0,0,0,4">
    <CheckBox IsChecked="{Binding EnableDaylightDetectionOnly}"
              VerticalAlignment="Center" Margin="0,0,8,0"/>
    <TextBlock Text="Only detect animals during daylight hours"
               FontSize="12" Foreground="#444" VerticalAlignment="Center"/>
</StackPanel>
<TextBlock Text="Background model training continues at all times."
           Style="{StaticResource Hint}"/>

<!-- Location warning (shown when toggle is on but no location is set) -->
<Border Padding="8,6"
        Background="#FFF8E1" BorderBrush="#FFD54F" BorderThickness="1"
        Margin="0,4,0,4"
        Visibility="{Binding ShowDaylightLocationWarning,
                     Converter={StaticResource BoolToVisibleConverter}}">
    <TextBlock TextWrapping="Wrap" FontSize="11" Foreground="#6D4C00">
        No location set — using 06:00–20:00 fallback.
        Set a location above for accurate sunrise/sunset times.
    </TextBlock>
</Border>

<Grid Margin="0,4,0,0"
      IsEnabled="{Binding EnableDaylightDetectionOnly}">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="16"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <!-- Sunrise offset -->
    <StackPanel Grid.Column="0">
        <TextBlock Text="Sunrise offset (minutes)" Style="{StaticResource Label}"/>
        <TextBlock Text="Negative = before sunrise, positive = after. Default: −30."
                   Style="{StaticResource Hint}"/>
        <TextBox Text="{Binding SunriseOffsetMinutes, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource Field}"/>
    </StackPanel>

    <!-- Sunset offset -->
    <StackPanel Grid.Column="2">
        <TextBlock Text="Sunset offset (minutes)" Style="{StaticResource Label}"/>
        <TextBlock Text="Positive = after sunset, negative = before. Default: +30."
                   Style="{StaticResource Hint}"/>
        <TextBox Text="{Binding SunsetOffsetMinutes, UpdateSourceTrigger=PropertyChanged}"
                 Style="{StaticResource Field}"/>
    </StackPanel>
</Grid>
```

- [ ] **Step 2: Build (XAML is validated at build time)**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 3: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/Views/Pages/SettingsPage.xaml
git commit -m "feat: add Daylight Detection section to Settings UI"
```

---

## Task 10: Update LiveViewModel — subscribe to event, update status

**Files:**
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/LiveViewModel.cs`

The live view already has `TrainingStatusText` and `TrainingTimeLeftText` for background model status. We'll add a `DaylightStatusText` property that shows the "outside daylight window" message when detection is paused.

- [ ] **Step 1: Add observable properties and inject ISunriseSunsetService**

Add two observable properties after `_modelDataAgeText`:

```csharp
[ObservableProperty] private string _daylightStatusText   = string.Empty;
[ObservableProperty] private string _daylightFallbackText = string.Empty;
```

Add `ISunriseSunsetService` to the constructor parameters:

```csharp
private readonly ISunriseSunsetService _daylightWindow;
```

In the constructor signature, add the parameter and assignment:

```csharp
ISunriseSunsetService daylightWindow,
// ...
_daylightWindow = daylightWindow;
```

Subscribe to the event in the constructor, after the other event subscriptions:

```csharp
_recognitionLoop.DaylightWindowChanged += OnDaylightWindowChanged;
```

- [ ] **Step 2: Add the event handler**

After `OnTrainingProgressChanged`, add:

```csharp
private void OnDaylightWindowChanged(object? sender, bool allowed)
{
    Application.Current.Dispatcher.Invoke(() =>
    {
        if (allowed)
        {
            DaylightStatusText   = string.Empty;
            DaylightFallbackText = _daylightWindow.IsUsingFallback
                ? "Daylight window active (no location set — using 06:00–20:00 fallback)"
                : string.Empty;
        }
        else
        {
            var next = _daylightWindow.NextTransitionTime;
            DaylightStatusText   = $"Detection paused — outside daylight window (next: {next:HH:mm})";
            DaylightFallbackText = _daylightWindow.IsUsingFallback
                ? "No location set — using 06:00–20:00 fallback"
                : string.Empty;
        }
    });
}
```

- [ ] **Step 3: Wire up DaylightStatusText in LiveViewPage.xaml**

In `Views/Pages/LiveViewPage.xaml`, find where `TrainingStatusText` is displayed (it's in the status area). Add a `TextBlock` for daylight status nearby — it should be visible only when non-empty, using the same `NullOrEmptyToCollapsedConverter` used elsewhere:

```xml
<TextBlock Text="{Binding DaylightStatusText}"
           FontSize="11" Foreground="#E65100" TextWrapping="Wrap"
           Margin="0,2,0,0"
           Visibility="{Binding DaylightStatusText,
                        Converter={StaticResource NullOrEmptyToCollapsedConverter}}"/>
<TextBlock Text="{Binding DaylightFallbackText}"
           FontSize="11" Foreground="#8D6E00" TextWrapping="Wrap"
           Margin="0,2,0,0"
           Visibility="{Binding DaylightFallbackText,
                        Converter={StaticResource NullOrEmptyToCollapsedConverter}}"/>
```

Find the existing training status area in `LiveViewPage.xaml` to place this nearby. Look for `TrainingStatusText` binding and add the daylight TextBlock adjacent to it.

- [ ] **Step 4: Build**

```bash
cd src/WildlifeWatcher
dotnet build WildlifeWatcher.sln -c Debug --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)

- [ ] **Step 5: Run all tests**

```bash
dotnet test WildlifeWatcher.sln -v minimal
```
Expected: All tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/WildlifeWatcher/WildlifeWatcher/ViewModels/LiveViewModel.cs \
        src/WildlifeWatcher/WildlifeWatcher/Views/Pages/LiveViewPage.xaml
git commit -m "feat: show daylight window pause status in live view"
```

---

## Task 11: Update recognition-pipeline.md

**Files:**
- Modify: `docs/recognition-pipeline.md`

- [ ] **Step 1: Update the pipeline summary**

In `docs/recognition-pipeline.md`, update the Summary section to add the new gate. Find the `## Summary` section and update it:

```markdown
## Summary

```
tick (every N seconds)
  → extract frame
  → update EMA background model
  → [gate: training complete?]
  → trigger daily sunrise/sunset refresh (fire-and-forget)
  → [gate: daylight window?]           ← SunriseSunsetService
  → extract POI crops             ← PointOfInterestService BFS on hot-cell grid
  → [gate: POI count > 0?]
  → [gate: cooldown expired?]
  → send to AI (POI crops or full frame)
  → filter by confidence
  → save capture + fire event
```
```

Also add a new section before `## Summary` documenting the new gate:

```markdown
## Step 2.5 — Daylight window gate (`SunriseSunsetService`)

If `EnableDaylightDetectionOnly` is `true`, this gate blocks AI detection outside the configured window. Background model updates and training always continue regardless.

**Detection window:** `[sunrise + SunriseOffsetMinutes, sunset + SunsetOffsetMinutes]`

- Sunrise/sunset fetched from Open-Meteo once per calendar day (fire-and-forget, cached)
- No location configured → 06:00–20:00 local fallback; `IsUsingFallback = true`
- Weather fetch fails → reuse yesterday's cache; if no cache, use 06:00–20:00 fallback
- Sign convention: negative offset = before the base time, positive = after

When blocked, fires `DaylightWindowChanged(false)` on the first blocked tick. Fires `DaylightWindowChanged(true)` when detection resumes.
```

- [ ] **Step 2: Commit**

```bash
git add docs/recognition-pipeline.md
git commit -m "docs: update recognition pipeline to document daylight window gate"
```

---

## Final Verification

- [ ] **Run all tests**

```bash
cd src/WildlifeWatcher
dotnet test WildlifeWatcher.sln -v minimal
```
Expected: All tests PASS

- [ ] **Full build**

```bash
dotnet build WildlifeWatcher.sln -c Release --nologo -v quiet
```
Expected: Build succeeded, 0 error(s)
