# Phase 6 — Weather Data + Calendar View

**Version:** `v1.3.x` → `v1.4.0`

## Context
Adds two features: (1) weather data fetched at capture time and stored with each capture record, and (2) a calendar heat-map view inside the Gallery page. Location is configured in Settings via city/postcode search (Nominatim, free, no API key, international). Weather from Open-Meteo (free, no API key).

**Decisions:**
- Geocoding: Nominatim (OpenStreetMap) — city name or postcode, up to 5 candidates shown for confirmation
- Weather fields: temperature °C, condition (from WMO code), wind speed km/h, precipitation mm, sunrise, sunset
- Calendar: inside Gallery page, toggled via Species/Calendar buttons
- Month navigation: prev/next arrows
- Heat map: 0=`#F5F5F5`, 1–2=`#C8E6C9`, 3–5=`#66BB6A`, 6+=`#2E7D32`
- Day click: horizontal thumbnail strip shown below calendar grid

---

## New Files

| File | Purpose |
|---|---|
| `Services/Interfaces/IGeocodingService.cs` | `GeocodingResult` record + `SearchAsync` |
| `Services/Interfaces/IWeatherService.cs` | `WeatherSnapshot` record + `GetCurrentWeatherAsync` |
| `Services/NominatimGeocodingService.cs` | Nominatim HTTP implementation |
| `Services/OpenMeteoWeatherService.cs` | Open-Meteo HTTP impl + WMO code mapping |
| `ViewModels/CalendarDayViewModel.cs` | Per-cell data: date, count, heat color, IsToday, IsBlank |
| `Converters/NullToCollapsedConverter.cs` | `null` → `Collapsed` (struct/nullable value types only; string null checks reuse existing `NullOrEmptyToCollapsedConverter`) |
| `Converters/ZeroToCollapsedConverter.cs` | `int` 0 → `Collapsed` |
| `Converters/ZeroToEmptyStringConverter.cs` | `int` 0 → `""` (hides day number in blank cells) |
| EF migration `AddWeatherFields` | Auto-generated: `dotnet ef migrations add AddWeatherFields` |

## Modified Files

| File | Change |
|---|---|
| `Models/CaptureRecord.cs` | 6 nullable weather fields |
| `Models/AppConfiguration.cs` | `Latitude?`, `Longitude?`, `LocationName` |
| `Services/CaptureStorageService.cs` | Inject `IWeatherService`; enrich record on save |
| `ViewModels/GalleryViewModel.cs` | `CalendarView` enum value + calendar state/commands |
| `ViewModels/SettingsViewModel.cs` | Location search fields + commands |
| `Views/Pages/GalleryPage.xaml` | Toggle bar + calendar panel |
| `Views/Pages/SettingsPage.xaml` | Location search section |
| `Views/Dialogs/CaptureDetailDialog.xaml` | Weather display rows |
| `App.xaml.cs` | Named HTTP clients; new service registrations |
| `App.xaml` | Register `NullToCollapsedConverter` (new); `NullOrEmptyToCollapsedConverter` (existing) handles string null checks |
| `WildlifeWatcher.csproj` | Add `Microsoft.Extensions.Http` only if build fails without it (likely already pulled transitively via `Microsoft.Extensions.Hosting`) |

---

## Key Implementation Notes

### `CaptureRecord` — 6 new nullable fields
```csharp
public double? Temperature { get; set; }
public string? WeatherCondition { get; set; }
public double? WindSpeed { get; set; }
public double? Precipitation { get; set; }
public DateTime? Sunrise { get; set; }
public DateTime? Sunset { get; set; }
```

### `AppConfiguration` — location fields
```csharp
public double? Latitude { get; set; }
public double? Longitude { get; set; }
public string LocationName { get; set; } = string.Empty;
```

### EF migration
```bash
cd src/WildlifeWatcher/WildlifeWatcher
dotnet ef migrations add AddWeatherFields
```
All fields nullable → no `DEFAULT` needed. Applied automatically at startup.

### `NominatimGeocodingService`
- Named HTTP client `"nominatim"` with `User-Agent: WildlifeWatcher/1.0` (required by Nominatim policy)
- URL: `search?q={Uri.EscapeDataString(query)}&format=json&limit=5`
- JSON: `display_name` (string), `lat` (string), `lon` (string) — parse lat/lon via `double.Parse(..., InvariantCulture)`
- Wrap in `try/catch`, return empty list on failure

### `OpenMeteoWeatherService`
- Named HTTP client `"openmeteo"`
- URL: `v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,wind_speed_10m,precipitation&daily=sunrise,sunset&timezone=auto&forecast_days=1`
- Parse `current.*` and `daily.sunrise[0]` / `daily.sunset[0]` via `JsonDocument`
- WMO code → condition string:

| Code | Condition |
|---|---|
| 0 | Clear |
| 1–3 | Partly Cloudy |
| 45, 48 | Fog |
| 51–57 | Drizzle |
| 61–67 | Rain |
| 71–77 | Snow |
| 80–82 | Showers |
| 85, 86 | Snow Showers |
| 95 | Thunderstorm |
| 96, 99 | Thunderstorm w/ Hail |
| _ | Unknown |

- Return `null` on any exception (capture still saved without weather)

### `App.xaml.cs` changes
`AddDbContextFactory` is already in use — do **not** re-apply it. Only add the named HTTP clients and service registrations below:
```csharp
// Named HTTP clients
services.AddHttpClient("nominatim", c => {
    c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("WildlifeWatcher/1.0");
});
services.AddHttpClient("openmeteo", c =>
    c.BaseAddress = new Uri("https://api.open-meteo.com/"));

services.AddSingleton<IGeocodingService, NominatimGeocodingService>();
services.AddSingleton<IWeatherService, OpenMeteoWeatherService>();
```

### `CaptureStorageService` — weather enrichment
```csharp
var cfg = _settings.CurrentSettings;
WeatherSnapshot? weather = null;
if (cfg.Latitude.HasValue && cfg.Longitude.HasValue)
    weather = await _weatherService.GetCurrentWeatherAsync(cfg.Latitude.Value, cfg.Longitude.Value);

record.Temperature     = weather?.Temperature;
record.WeatherCondition = weather?.Condition;
record.WindSpeed       = weather?.WindSpeed;
record.Precipitation   = weather?.Precipitation;
record.Sunrise         = weather?.Sunrise;
record.Sunset          = weather?.Sunset;
```

### `CalendarDayViewModel`
```csharp
public class CalendarDayViewModel {
    public DateTime? Date { get; }
    public int DayNumber { get; }        // 0 for blank padding cells
    public int CaptureCount { get; }
    public string HeatColor { get; }    // based on count
    public bool IsToday { get; }
    public bool IsBlank { get; }
    public static CalendarDayViewModel Blank() => ...;
}
```
Heat map: `0=#F5F5F5`, `1-2=#C8E6C9`, `3-5=#66BB6A`, `6+=#2E7D32`

### `GalleryViewModel` — calendar additions
```csharp
public enum GalleryView { SpeciesList, SpeciesDetail, CalendarView }

[ObservableProperty] int _calendarYear = DateTime.Today.Year;
[ObservableProperty] int _calendarMonth = DateTime.Today.Month;
[ObservableProperty] DateTime? _selectedDate;
public string CalendarMonthLabel => new DateTime(CalendarYear, CalendarMonth, 1).ToString("MMMM yyyy");
public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = new();
public ObservableCollection<CaptureCardViewModel> SelectedDayCaptures { get; } = new();
```

**`LoadCalendarAsync`** (private):
- Query all `CaptureRecords` for month from DB
- Group by `.CapturedAt.Date` → count dictionary
- Monday-first offset: `offset = dow == 0 ? 6 : dow - 1`
- Add blank cells + `CalendarDayViewModel` per day

**Two separate nav commands** (avoids WPF `CommandParameter` string→int issue):
```csharp
[RelayCommand] Task NavigateCalendarForward() → AddMonths(+1) → LoadCalendarAsync()
[RelayCommand] Task NavigateCalendarBack()    → AddMonths(-1) → LoadCalendarAsync()
```

**`SelectDate` command** (`RelayCommand<CalendarDayViewModel>`):
- Skip if blank or count == 0
- Query DB for captures on that date with `.Include(r => r.Species)`
- Populate `SelectedDayCaptures`

### `SettingsPage.xaml` — Location section (before Data & Storage)
```
TextBlock "Location"
TextBlock "Used for weather data paired with captures."
DockPanel:
  Button "Search"  DockPanel.Dock=Right  Command=SearchLocationCommand
  TextBox {LocationQuery}  placeholder="City name or postcode..."
TextBlock {LocationSearchStatus}  FontSize=11

ItemsControl {LocationResults} (ZeroToCollapsed on .Count):
  DataTemplate: Border Cursor=Hand
    TextBlock {DisplayName}
    MouseBinding → SelectLocationCommand param={Binding}

TextBlock "Current: {LocationName}" (NullOrEmptyToCollapsed on LocationName — reuses existing converter)
```

### `GalleryPage.xaml` — Calendar additions

**Toggle bar** (always visible, DockPanel.Dock=Top):
```xml
<StackPanel Orientation="Horizontal" Background="#E8F0E8" Padding="8,6">
  <Button Content="Species" Command="{Binding ShowSpeciesListCommand}"/>
  <Button Content="Calendar" Command="{Binding ShowCalendarCommand}" Margin="4,0,0,0"/>
</StackPanel>
```

**Calendar panel** (Visibility=IsShowingCalendarView):
```
DockPanel:
  Month nav (Dock=Top): [‹] [MMMM yyyy] [›]
    NavigateCalendarBackCommand / NavigateCalendarForwardCommand
  Day headers (Dock=Top): UniformGrid Columns=7 — Mon Tue Wed Thu Fri Sat Sun
  Day strip (Dock=Bottom, NullToCollapsed on SelectedDate):
    "{SelectedDateLabel} — {count} capture(s)"
    ScrollViewer (horizontal) > ItemsControl SelectedDayCaptures
      StackPanel Orientation=Horizontal
      Click thumbnail → CaptureDetailDialog
  Calendar grid (fills remaining):
    ItemsControl CalendarDays:
      ItemsPanel: UniformGrid Columns=7
      DataTemplate:
        Border Background={HeatColor} MinHeight=52 Margin=1
          SelectDateCommand CommandParameter={Binding}
          Today ring: inner Border BorderBrush=#2D5A27 BorderThickness=2 (visible when IsToday)
          DayNumber (ZeroToEmptyString for blank cells)
          CaptureCount (ZeroToCollapsed)
```

### `CaptureDetailDialog.xaml` — weather rows
```xml
<!-- Collapsed when WeatherCondition is null/empty — uses existing NullOrEmptyToCollapsedConverter (string) -->
<StackPanel Orientation="Horizontal" Margin="0,6,0,0"
    Visibility="{Binding WeatherCondition, Converter={StaticResource NullOrEmptyToCollapsedConverter}}">
  <TextBlock Text="{Binding WeatherCondition}"/>
  <TextBlock Text="{Binding Temperature, StringFormat=' • {0:F1}°C'}"/>
  <TextBlock Text="{Binding WindSpeed, StringFormat=' • {0:F0} km/h wind'}"/>
  <TextBlock Text="{Binding Precipitation, StringFormat=' • {0:F1}mm rain'}"/>
</StackPanel>
<!-- Collapsed when Sunrise is null — uses new NullToCollapsedConverter (DateTime? nullable struct) -->
<StackPanel Orientation="Horizontal" Margin="0,2,0,0"
    Visibility="{Binding Sunrise, Converter={StaticResource NullToCollapsedConverter}}">
  <TextBlock Text="{Binding Sunrise, StringFormat='Sunrise: {0:HH:mm}'}"/>
  <TextBlock Text="{Binding Sunset,  StringFormat='  Sunset: {0:HH:mm}'}"/>
</StackPanel>
```

---

## Known Limitations
- `FrameExtractionIntervalSeconds` change requires app restart (captured at loop start)
- Nominatim postcode accuracy varies by country (best for UK, US, DE; patchy elsewhere) — results shown for user confirmation before saving
- Weather is fetched at detection time; historical weather cannot be backfilled for existing captures

---

## Verification

1. `dotnet build` — 0 errors
2. `dotnet ef migrations add AddWeatherFields` → migration generated
3. **City search:** Settings → type "London" → Search → results appear → click → "Current: London, England, UK"
4. **Postcode search:** type "SW1A 1AA" → result shows Westminster area → select
5. **Save:** `settings.json` contains `Latitude`, `Longitude`, `LocationName`
6. **Weather on capture:** with location saved, trigger detection → CaptureDetailDialog shows weather row
7. **No location:** capture without location → weather rows hidden
8. **Calendar toggle:** Gallery → "Calendar" button → current month appears
9. **Heat map:** dates with captures show correct green shading; 0 = near-white
10. **Today ring:** today's cell has dark green border
11. **Month nav:** ‹/› navigate correctly; label updates
12. **Day click:** captures appear in horizontal strip below calendar
13. **Thumbnail in strip → dialog:** CaptureDetailDialog opens with weather rows
14. **Species toggle:** "Species" button → returns to species grid
