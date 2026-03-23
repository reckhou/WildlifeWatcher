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

    // ── NextTransitionTime helpers ────────────────────────────────────────

    [Fact]
    public void IsWithinWindowAt_InsideWindow_StillTrue()
    {
        var svc        = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var windowStart = DateTime.Today.AddHours(6);
        var windowEnd   = DateTime.Today.AddHours(20);
        var now         = DateTime.Today.AddHours(12);

        Assert.True(svc.IsWithinWindowAt(now, windowStart, windowEnd));
    }

    [Fact]
    public void ComputeNextStart_BeforeWindow_NotInWindow()
    {
        var svc        = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var windowStart = DateTime.Today.AddHours(6);
        var windowEnd   = DateTime.Today.AddHours(20);
        var now         = DateTime.Today.AddHours(5);

        Assert.False(svc.IsWithinWindowAt(now, windowStart, windowEnd));
    }

    [Fact]
    public void ComputeNextStart_AfterWindow_NotInWindow()
    {
        var svc        = new SunriseSunsetService(Mock.Of<IWeatherService>(), NullLogger<SunriseSunsetService>.Instance);
        var windowStart = DateTime.Today.AddHours(6);
        var windowEnd   = DateTime.Today.AddHours(20);
        var now         = DateTime.Today.AddHours(21);

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
        await svc.RefreshIfNeededAsync(config);

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
