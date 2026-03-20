namespace WildlifeWatcher.Services.Interfaces;

public record GeocodingResult(string DisplayName, double Latitude, double Longitude);

public interface IGeocodingService
{
    Task<IReadOnlyList<GeocodingResult>> SearchAsync(string query);
}
