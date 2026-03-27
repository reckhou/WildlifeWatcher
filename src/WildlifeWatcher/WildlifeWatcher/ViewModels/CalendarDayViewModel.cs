using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.ViewModels;

public class CalendarDayViewModel
{
    public DateTime? Date         { get; }
    public int       DayNumber    { get; }
    public int       CaptureCount { get; }
    /// <summary>0.0 = no captures, scales to 1.0 at 20+ captures (calibrated 5–20 range).</summary>
    public double    HeatOpacity  { get; }
    public bool      IsToday      { get; }
    public bool      IsBlank      { get; }

    // Display strings — empty means hide the element
    public string WeatherLabel     { get; }  // "⛅ Partly cloudy"
    public string TemperatureLabel { get; }  // "🌡️ 18°C"
    public string PrecipLabel      { get; }  // "💧 2.1mm"
    public string SunriseLabel     { get; }  // "🌅 06:45"
    public string SunsetLabel      { get; }  // "🌇 19:20"
    public string ShotCountLabel   { get; }  // "📷 12"

    public bool HasWeather     => !string.IsNullOrEmpty(WeatherLabel);
    public bool HasTemperature => !string.IsNullOrEmpty(TemperatureLabel);
    public bool HasPrecip      => !string.IsNullOrEmpty(PrecipLabel);
    public bool HasSunTimes    => !string.IsNullOrEmpty(SunriseLabel);
    public bool HasShots       => CaptureCount > 0;

    public CalendarDayViewModel(DateTime date, int count, DailySummary? summary = null)
    {
        Date         = date;
        DayNumber    = date.Day;
        CaptureCount = count;
        IsToday      = date.Date == DateTime.Today;
        IsBlank      = false;

        // Opacity: 0 for no captures; 0.15 minimum for any captures;
        // scales linearly to 1.0 over the 5–20 shot range.
        HeatOpacity = count <= 0 ? 0.0
                    : count <= 5 ? 0.15
                    : Math.Min(0.15 + (count - 5.0) / 15.0 * 0.85, 1.0);

        WeatherLabel     = string.IsNullOrEmpty(summary?.WeatherCondition)
                           ? string.Empty : $"⛅ {summary!.WeatherCondition}";
        TemperatureLabel = summary?.Temperature.HasValue == true
                           ? $"🌡️ {summary.Temperature.Value:0}°C" : string.Empty;
        PrecipLabel      = summary?.Precipitation is > 0
                           ? $"💧 {summary.Precipitation.Value:0.#}mm" : string.Empty;
        SunriseLabel     = summary?.Sunrise.HasValue == true
                           ? $"🌅 {summary.Sunrise.Value:HH:mm}" : string.Empty;
        SunsetLabel      = summary?.Sunset.HasValue == true
                           ? $"🌇 {summary.Sunset.Value:HH:mm}" : string.Empty;
        ShotCountLabel   = count > 0 ? $"📷 {count}" : string.Empty;
    }

    private CalendarDayViewModel()
    {
        IsBlank        = true;
        WeatherLabel   = string.Empty;
        TemperatureLabel = string.Empty;
        PrecipLabel    = string.Empty;
        SunriseLabel   = string.Empty;
        SunsetLabel    = string.Empty;
        ShotCountLabel = string.Empty;
    }

    public static CalendarDayViewModel Blank() => new();
}
