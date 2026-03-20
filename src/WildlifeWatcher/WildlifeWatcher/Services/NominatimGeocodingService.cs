using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class NominatimGeocodingService : IGeocodingService
{
    private readonly HttpClient _http;

    public NominatimGeocodingService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("nominatim");
    }

    public async Task<IReadOnlyList<GeocodingResult>> SearchAsync(string query)
    {
        try
        {
            var url  = $"search?q={Uri.EscapeDataString(query)}&format=json&limit=5";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var results = new List<GeocodingResult>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("display_name").GetString() ?? string.Empty;
                var lat  = double.Parse(item.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
                var lon  = double.Parse(item.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
                results.Add(new GeocodingResult(name, lat, lon));
            }
            return results;
        }
        catch
        {
            return Array.Empty<GeocodingResult>();
        }
    }
}
