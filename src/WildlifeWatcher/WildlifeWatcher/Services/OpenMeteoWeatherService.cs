using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class OpenMeteoWeatherService : IWeatherService
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenMeteoWeatherService> _logger;

    public OpenMeteoWeatherService(IHttpClientFactory factory, ILogger<OpenMeteoWeatherService> logger)
    {
        _http   = factory.CreateClient("openmeteo");
        _logger = logger;
    }

    public async Task<WeatherSnapshot?> GetCurrentWeatherAsync(double latitude, double longitude)
    {
        try
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var url = $"v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,weather_code,wind_speed_10m,precipitation" +
                      "&daily=sunrise,sunset&timezone=auto&forecast_days=1";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var current = doc.RootElement.GetProperty("current");
            var daily   = doc.RootElement.GetProperty("daily");

            var temp       = current.GetProperty("temperature_2m").GetDouble();
            var code       = current.GetProperty("weather_code").GetInt32();
            var wind       = current.GetProperty("wind_speed_10m").GetDouble();
            var precip     = current.GetProperty("precipitation").GetDouble();

            var sunriseStr = daily.GetProperty("sunrise")[0].GetString();
            var sunsetStr  = daily.GetProperty("sunset")[0].GetString();

            DateTime? sunrise = sunriseStr is not null ? DateTime.Parse(sunriseStr) : null;
            DateTime? sunset  = sunsetStr  is not null ? DateTime.Parse(sunsetStr)  : null;

            return new WeatherSnapshot(temp, WmoToCondition(code), wind, precip, sunrise, sunset);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch weather data");
            return null;
        }
    }

    private static string WmoToCondition(int code) => code switch
    {
        0                       => "Clear",
        1 or 2 or 3             => "Partly Cloudy",
        45 or 48                => "Fog",
        51 or 53 or 55
            or 56 or 57         => "Drizzle",
        61 or 63 or 65
            or 66 or 67         => "Rain",
        71 or 73 or 75 or 77    => "Snow",
        80 or 81 or 82          => "Showers",
        85 or 86                => "Snow Showers",
        95                      => "Thunderstorm",
        96 or 99                => "Thunderstorm w/ Hail",
        _                       => "Unknown"
    };
}
