namespace WildlifeWatcher.Services.Interfaces;

public record WeatherSnapshot(
    double    Temperature,
    string    Condition,
    double    WindSpeed,
    double    Precipitation,
    DateTime? Sunrise,
    DateTime? Sunset);

public interface IWeatherService
{
    Task<WeatherSnapshot?> GetCurrentWeatherAsync(double latitude, double longitude);
}
