# Calendar & Gallery Improvements Implementation Plan

**Goal:** Six improvements: calendar heat redesign, richer weather cells, calendar-to-species navigation with auto day filter, bidirectional species sorting, and Reset Gallery moved to Settings.

**Architecture:** `DailySummary` is widened with full weather fields; `CalendarDayViewModel` computes all display strings and the new `HeatOpacity` scalar. A new `SpeciesListDateFilter` state on `GalleryViewModel` drives filtered species loading and auto-applies the existing day-filter mechanism when drilling into a species. Bidirectional sort is a `SortDescending` bool alongside the existing `SortMode`. Reset Gallery moves to `SettingsViewModel` via a new `GalleryReset` event on `ICaptureStorageService` that `GalleryViewModel` subscribes to for automatic reload.

**Tech Stack:** C# + WPF + CommunityToolkit.Mvvm + EF Core

---

## Progress

- [x] Task 1: Widen `DailySummary` and update `GetCaptureDailySummaryForMonthAsync`
- [x] Task 2: Refactor `CalendarDayViewModel` — heat opacity, emoji weather labels, shot count label
- [x] Task 3: Update calendar cell XAML — white background, accent bar, new label bindings
- [x] Task 4: Add `GetSpeciesSummariesForDateAsync`; wire calendar day click to filtered species list
- [x] Task 5: Update Species List XAML — date filter badge with clear button
- [x] Task 6: Bidirectional sort — `SortDescending` in VM + dynamic button labels
- [x] Task 7: Update sort button XAML for ▲/▼ and active styling
- [x] Task 8: Move Reset Gallery to Settings — `GalleryReset` event + `SettingsViewModel` command

---

## Files

- Modify: `Services/Interfaces/ICaptureStorageService.cs` — widen `DailySummary`; add `GetSpeciesSummariesForDateAsync`; add `GalleryReset` event
- Modify: `Services/CaptureStorageService.cs` — implement widened query; new date-species query; fire `GalleryReset` event
- Modify: `ViewModels/CalendarDayViewModel.cs` — new constructor params, computed display strings, `HeatOpacity`
- Modify: `ViewModels/GalleryViewModel.cs` — `SpeciesListDateFilter`, `SelectDate` navigation, `OpenSpecies` auto day-filter, `SortDescending`, label properties, subscribe to `GalleryReset`, remove `ResetGalleryCommand`
- Modify: `ViewModels/SettingsViewModel.cs` — inject `ICaptureStorageService`, add `ResetGalleryCommand`
- Modify: `Views/Pages/GalleryPage.xaml` — calendar cell template, species list date badge, sort buttons
- Modify: `Views/Pages/SettingsPage.xaml` — add Reset Gallery in Data & Storage section
- Modify: `App.xaml.cs` (or DI registration) — add `ICaptureStorageService` to `SettingsViewModel` DI

---

### Task 1: Widen `DailySummary` and update `GetCaptureDailySummaryForMonthAsync`

**Files:** `Services/Interfaces/ICaptureStorageService.cs`, `Services/CaptureStorageService.cs`

Widen the record:
```csharp
public record DailySummary(
    int       Count,
    string?   WeatherCondition,
    double?   Temperature,
    double?   Precipitation,
    DateTime? Sunrise,
    DateTime? Sunset);
```

Update the query to pull all five weather fields — take the first non-null value within each day's group:
```csharp
public async Task<Dictionary<DateTime, DailySummary>> GetCaptureDailySummaryForMonthAsync(int year, int month)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var start = new DateTime(year, month, 1);
    var end   = start.AddMonths(1);

    var rows = await db.CaptureRecords
        .Where(c => c.CapturedAt >= start && c.CapturedAt < end)
        .Select(c => new {
            c.CapturedAt,
            c.WeatherCondition,
            c.Temperature,
            c.Precipitation,
            c.Sunrise,
            c.Sunset
        })
        .ToListAsync();

    return rows
        .GroupBy(c => c.CapturedAt.Date)
        .ToDictionary(
            g => g.Key,
            g => new DailySummary(
                g.Count(),
                g.Select(c => c.WeatherCondition).FirstOrDefault(w => w != null),
                g.Select(c => c.Temperature).FirstOrDefault(v => v.HasValue),
                g.Select(c => c.Precipitation).FirstOrDefault(v => v.HasValue),
                g.Select(c => c.Sunrise).FirstOrDefault(v => v.HasValue),
                g.Select(c => c.Sunset).FirstOrDefault(v => v.HasValue)));
}
```

**Verify:** Project compiles — all callers of `DailySummary` that use positional construction now pass 6 args (there's only one site: `LoadCalendarAsync` in `GalleryViewModel`). Fix that call to pass `null` for the four new fields until Task 2 wires them properly. Actually, `GalleryViewModel` reads from the dictionary result, not constructing `DailySummary` directly, so only the record definition needs updating.

---

### Task 2: Refactor `CalendarDayViewModel`

**Files:** `ViewModels/CalendarDayViewModel.cs`

**Depends on:** Task 1

Replace `HeatColor` with `HeatOpacity` and add weather display strings. Full new class:

```csharp
public class CalendarDayViewModel
{
    public DateTime? Date             { get; }
    public int       DayNumber        { get; }
    public int       CaptureCount     { get; }
    public double    HeatOpacity      { get; }   // 0.0 = no captures, 1.0 = 20+ captures
    public bool      IsToday          { get; }
    public bool      IsBlank          { get; }

    // Weather display strings (empty string = hide)
    public string WeatherLabel      { get; }   // "⛅ Partly cloudy"
    public string TemperatureLabel  { get; }   // "🌡️ 18°C"
    public string PrecipLabel       { get; }   // "💧 2.1mm"
    public string SunriseLabel      { get; }   // "🌅 06:45"
    public string SunsetLabel       { get; }   // "🌇 19:20"
    public string ShotCountLabel    { get; }   // "📷 12"

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

        // Heat opacity: 0 for no captures, scaled from 0.15 → 1.0 over range 5–20 shots
        HeatOpacity = count <= 0  ? 0.0
                    : count <= 5  ? 0.15
                    : Math.Min(0.15 + (count - 5.0) / 15.0 * 0.85, 1.0);

        WeatherLabel     = FormatWeather(summary?.WeatherCondition);
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

    private CalendarDayViewModel() { IsBlank = true; }
    public static CalendarDayViewModel Blank() => new();

    private static string FormatWeather(string? condition) =>
        string.IsNullOrEmpty(condition) ? string.Empty : $"⛅ {condition}";
}
```

Update `GalleryViewModel.LoadCalendarAsync` to pass the `DailySummary` directly:
```csharp
CalendarDays.Add(new CalendarDayViewModel(date, summary?.Count ?? 0, summary));
```
(Previously passed `summary?.WeatherCondition` — now passes the whole summary.)

**Verify:** Compiles. Existing `HasWeather` and `WeatherCondition` references in XAML will need updating in Task 3.

---

### Task 3: Update calendar cell XAML

**Files:** `Views/Pages/GalleryPage.xaml`

**Depends on:** Task 2

Replace the `DataTemplate` for `CalendarDays` ItemsControl. Key changes:
- `Border Background="White"` (was `HeatColor`)
- Replace the single count/weather `TextBlock`s with the new label bindings
- Add a `Rectangle` accent bar at the bottom using a `DockPanel` layout

New cell template structure:
```xaml
<DataTemplate>
    <Border Background="White" MinHeight="60" Margin="1"
            BorderBrush="#DDDDDD" BorderThickness="1"
            Cursor="Hand">
        <Border.InputBindings>
            <MouseBinding MouseAction="LeftClick"
                          Command="{Binding DataContext.SelectDateCommand,
                                    RelativeSource={RelativeSource AncestorType=UserControl}}"
                          CommandParameter="{Binding}"/>
        </Border.InputBindings>
        <Border BorderThickness="2">
            <Border.Style>
                <Style TargetType="Border">
                    <Setter Property="BorderBrush" Value="Transparent"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsToday}" Value="True">
                            <Setter Property="BorderBrush" Value="#2D5A27"/>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding IsBlank}" Value="True">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Border.Style>
            <DockPanel>
                <!-- Accent bar at bottom: opacity scales with capture density -->
                <Rectangle DockPanel.Dock="Bottom" Height="3"
                           Fill="#2D5A27" Opacity="{Binding HeatOpacity}"/>
                <StackPanel Margin="4,3,4,3">
                    <!-- Day number -->
                    <TextBlock Text="{Binding DayNumber,
                                              Converter={StaticResource ZeroToEmptyStringConverter}}"
                               FontSize="12" FontWeight="SemiBold" Foreground="#222"/>
                    <!-- Weather condition -->
                    <TextBlock Text="{Binding WeatherLabel}" FontSize="9" Foreground="#444"
                               TextWrapping="Wrap"
                               Visibility="{Binding HasWeather,
                                            Converter={StaticResource BoolToVisibleConverter}}"/>
                    <!-- Temperature -->
                    <TextBlock Text="{Binding TemperatureLabel}" FontSize="9" Foreground="#333"
                               Visibility="{Binding HasTemperature,
                                            Converter={StaticResource BoolToVisibleConverter}}"/>
                    <!-- Precipitation -->
                    <TextBlock Text="{Binding PrecipLabel}" FontSize="9" Foreground="#333"
                               Visibility="{Binding HasPrecip,
                                            Converter={StaticResource BoolToVisibleConverter}}"/>
                    <!-- Sunrise / Sunset on one line -->
                    <StackPanel Orientation="Horizontal"
                                Visibility="{Binding HasSunTimes,
                                             Converter={StaticResource BoolToVisibleConverter}}">
                        <TextBlock Text="{Binding SunriseLabel}" FontSize="9" Foreground="#333"
                                   Margin="0,0,4,0"/>
                        <TextBlock Text="{Binding SunsetLabel}" FontSize="9" Foreground="#333"/>
                    </StackPanel>
                    <!-- Shot count — shown last -->
                    <TextBlock Text="{Binding ShotCountLabel}" FontSize="9"
                               FontWeight="SemiBold" Foreground="#2D5A27"
                               Visibility="{Binding HasShots,
                                            Converter={StaticResource BoolToVisibleConverter}}"/>
                </StackPanel>
            </DockPanel>
        </Border>
    </Border>
</DataTemplate>
```

Also remove the old `SelectedDate` horizontal strip (`Border DockPanel.Dock="Bottom"` with `SelectedDayCaptures` ListBox) — it's replaced by the species list navigation in Task 4. The `SelectedDateLabel` TextBlock header can be removed too.

**Verify:** Calendar cells show white backgrounds. Days with captures have a subtle green bar at the bottom with intensity proportional to count. Weather/temperature/precip/sunrise/sunset/shot count all display when data is present.

---

### Task 4: Calendar day click → filtered species list with auto day-filter drill-in

**Files:** `Services/Interfaces/ICaptureStorageService.cs`, `Services/CaptureStorageService.cs`, `ViewModels/GalleryViewModel.cs`

**Add `GetSpeciesSummariesForDateAsync` to interface and service:**
```csharp
Task<IReadOnlyList<SpeciesSummary>> GetSpeciesSummariesForDateAsync(DateTime date);
```

Implementation mirrors `GetAllSpeciesSummariesAsync` but with a date filter:
```csharp
public async Task<IReadOnlyList<SpeciesSummary>> GetSpeciesSummariesForDateAsync(DateTime date)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var start = date.Date;
    var end   = start.AddDays(1);
    return await db.Species
        .Include(s => s.Captures)
        .Where(s => s.Captures.Any(c => c.CapturedAt >= start && c.CapturedAt < end))
        .Select(s => new SpeciesSummary(
            s.Id, s.CommonName, s.ScientificName, s.Description,
            s.FirstDetectedAt, s.ReferencePhotoPath,
            s.Captures.Count,
            s.Captures.Any() ? s.Captures.Max(c => c.CapturedAt) : s.FirstDetectedAt,
            s.Captures.OrderByDescending(c => c.CapturedAt).Select(c => c.ImageFilePath).FirstOrDefault()))
        .ToListAsync();
}
```

**Add state to `GalleryViewModel`:**
```csharp
[ObservableProperty] private DateTime? _speciesListDateFilter;

public bool IsSpeciesListDateFiltered => SpeciesListDateFilter.HasValue;
public string SpeciesListDateLabel =>
    SpeciesListDateFilter.HasValue
        ? $"📅 {SpeciesListDateFilter.Value:d MMMM yyyy}"
        : string.Empty;

partial void OnSpeciesListDateFilterChanged(DateTime? value)
{
    OnPropertyChanged(nameof(IsSpeciesListDateFiltered));
    OnPropertyChanged(nameof(SpeciesListDateLabel));
}
```

**Rewrite `SelectDateCommand`** to navigate to species list (remove SelectedDayCaptures loading):
```csharp
[RelayCommand]
private async Task SelectDate(CalendarDayViewModel? day)
{
    if (day is null || day.IsBlank || day.CaptureCount == 0) return;
    var date = day.Date!.Value;
    var summaries = await _captureStorage.GetSpeciesSummariesForDateAsync(date);
    _allSpecies = summaries.Select(s => new SpeciesCardViewModel(s)).ToList();
    SpeciesListDateFilter = date;
    ApplyFilter();
    CurrentView = GalleryView.SpeciesList;
}
```

**Add `ClearSpeciesDateFilterCommand`:**
```csharp
[RelayCommand]
private async Task ClearSpeciesDateFilter()
{
    SpeciesListDateFilter = null;
    await LoadAsync(); // reloads _allSpecies from full list
}
```

**Update `OpenSpecies` to auto-apply day filter** when coming from a date-filtered list:
```csharp
[RelayCommand]
private async Task OpenSpecies(SpeciesCardViewModel card)
{
    _selectedSpecies               = card;
    SelectedSpeciesName            = card.Summary.CommonName;
    SelectedSpeciesScientificName  = card.Summary.ScientificName;
    await LoadCapturesAsync(card.Summary.SpeciesId);
    CurrentView = GalleryView.SpeciesDetail;

    // Auto-apply day filter when drilling in from the calendar date view
    if (SpeciesListDateFilter.HasValue)
    {
        var d = SpeciesListDateFilter.Value;
        FilterYear  = d.Year;
        FilterMonth = d.Month;
        FilterDay   = d.Day;
        await FilterByDayAsync();
    }
}
```

Also update `Back()` to restore the full species list if a date filter was active:
```csharp
[RelayCommand]
private async Task BackAsync()   // rename from Back() to BackAsync() since it's now async
{
    IsSelectionMode = false;
    IsFilteredByDay = false;
    if (SpeciesListDateFilter.HasValue)
    {
        // Stay in species list but keep the date filter
        CurrentView = GalleryView.SpeciesList;
    }
    else
    {
        CurrentView      = GalleryView.SpeciesList;
        _selectedSpecies = null;
    }
}
```

Remove `SelectedDayCaptures`, `SelectedDate`, `SelectedDateLabel`, `OpenDayCaptureCommand` from the VM — they're no longer used.

**Verify:** In the app, open Calendar view, click a day with captures — the view switches to the Species List showing only species seen that day with the date badge visible. Click a species — Species Detail opens with photos already filtered to that date.

---

### Task 5: Species List XAML — date filter badge

**Files:** `Views/Pages/GalleryPage.xaml`

**Depends on:** Task 4

In the Species List header area (the `Border DockPanel.Dock="Top"` containing the search box and sort buttons), add a date filter badge row that appears only when `IsSpeciesListDateFiltered` is true. Insert it as a second `DockPanel.Dock="Top"` border just below the main toolbar:

```xaml
<!-- Date filter badge (shown when navigated from calendar) -->
<Border DockPanel.Dock="Top"
        Visibility="{Binding IsSpeciesListDateFiltered,
                     Converter={StaticResource BoolToVisibleConverter}}"
        Background="#E8F5E9" Padding="12,6"
        BorderBrush="#DDDDDD" BorderThickness="0,0,0,1">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="{Binding SpeciesListDateLabel}"
                   FontSize="12" FontWeight="SemiBold" Foreground="#2D5A27"
                   VerticalAlignment="Center" Margin="0,0,12,0"/>
        <Button Content="✕ Show All Species"
                Command="{Binding ClearSpeciesDateFilterCommand}"
                Background="Transparent" Foreground="#2D5A27"
                BorderBrush="#2D5A27" BorderThickness="1"
                Padding="8,3" FontSize="11" Cursor="Hand"/>
    </StackPanel>
</Border>
```

**Verify:** Badge is invisible in normal gallery view. After clicking a calendar day, the badge appears with the formatted date and "Show All Species" button. Clicking the button clears the filter and reloads the full species list.

---

### Task 6: Bidirectional sort — `SortDescending` in `GalleryViewModel`

**Files:** `ViewModels/GalleryViewModel.cs`

Add `SortDescending` property and update sort logic:
```csharp
[ObservableProperty] private bool _sortDescending = false;

partial void OnSortDescendingChanged(bool value) => ApplyFilter();
```

Update `ApplyFilter` to respect direction:
```csharp
private void ApplyFilter()
{
    var q = SearchText.Trim();
    var source = /* existing filter logic — unchanged */;

    IOrderedEnumerable<SpeciesCardViewModel> sorted = SortMode switch
    {
        GallerySortMode.LatinNameAZ   => SortDescending
                                          ? source.OrderByDescending(s => s.Summary.ScientificName)
                                          : source.OrderBy(s => s.Summary.ScientificName),
        GallerySortMode.LatestCapture => SortDescending
                                          ? source.OrderBy(s => s.LatestCaptureAt)             // ASC = oldest first
                                          : source.OrderByDescending(s => s.LatestCaptureAt),  // DESC = newest first (default)
        GallerySortMode.TotalCaptures => SortDescending
                                          ? source.OrderBy(s => s.CaptureCount)
                                          : source.OrderByDescending(s => s.CaptureCount),
        _                             => SortDescending
                                          ? source.OrderByDescending(s => s.Summary.CommonName)
                                          : source.OrderBy(s => s.Summary.CommonName),
    };

    FilteredSpecies.Clear();
    foreach (var s in sorted) FilteredSpecies.Add(s);
}
```

Update `Sort` command to toggle direction when re-clicking the active mode:
```csharp
[RelayCommand]
private void Sort(string mode)
{
    var newMode = mode switch
    {
        "LatinNameAZ"   => GallerySortMode.LatinNameAZ,
        "LatestCapture" => GallerySortMode.LatestCapture,
        "TotalCaptures" => GallerySortMode.TotalCaptures,
        _               => GallerySortMode.NameAZ,
    };

    if (newMode == SortMode)
    {
        SortDescending = !SortDescending;  // toggle direction
    }
    else
    {
        // Default directions: Name/Latin = ASC, Latest/Count = DESC
        SortDescending = newMode == GallerySortMode.LatestCapture || newMode == GallerySortMode.TotalCaptures;
        SortMode = newMode;  // triggers OnSortModeChanged → ApplyFilter
    }
}
```

Add computed label properties (notify when `SortMode` or `SortDescending` changes):
```csharp
public string SortNameLabel   => SortMode == GallerySortMode.NameAZ        ? $"Name {Arrow()}"   : "Name";
public string SortLatinLabel  => SortMode == GallerySortMode.LatinNameAZ   ? $"Latin {Arrow()}"  : "Latin";
public string SortLatestLabel => SortMode == GallerySortMode.LatestCapture ? $"Latest {Arrow()}" : "Latest";
public string SortCountLabel  => SortMode == GallerySortMode.TotalCaptures ? $"Count {Arrow()}"  : "Count";
private string Arrow() => SortDescending ? "▼" : "▲";
```

In `OnSortModeChanged` and `OnSortDescendingChanged`, add:
```csharp
OnPropertyChanged(nameof(SortNameLabel));
OnPropertyChanged(nameof(SortLatinLabel));
OnPropertyChanged(nameof(SortLatestLabel));
OnPropertyChanged(nameof(SortCountLabel));
```

**Verify:** Compiles. Clicking "Latest" (active sort) toggles between newest-first and oldest-first.

---

### Task 7: Update sort button XAML

**Files:** `Views/Pages/GalleryPage.xaml`

**Depends on:** Task 6

Replace the four sort buttons' static `Content` strings with bindings to the computed label properties:

```xaml
<Button Content="{Binding SortNameLabel}"
        Command="{Binding SortCommand}" CommandParameter="NameAZ" .../>
<Button Content="{Binding SortLatinLabel}"
        Command="{Binding SortCommand}" CommandParameter="LatinNameAZ" .../>
<Button Content="{Binding SortLatestLabel}"
        Command="{Binding SortCommand}" CommandParameter="LatestCapture" .../>
<Button Content="{Binding SortCountLabel}"
        Command="{Binding SortCommand}" CommandParameter="TotalCaptures" .../>
```

All existing `Style` triggers on `SortMode` binding are kept unchanged — they still highlight the active button.

**Verify:** Sort buttons show "Name ▲", "Latin", "Latest ▼", "Count" initially (Latest active, DESC default). Clicking "Latest" again flips to "Latest ▲". Clicking "Name" activates it with "Name ▲" (ASC default).

---

### Task 8: Move Reset Gallery to Settings

**Files:** `Services/Interfaces/ICaptureStorageService.cs`, `Services/CaptureStorageService.cs`, `ViewModels/GalleryViewModel.cs`, `ViewModels/SettingsViewModel.cs`, `Views/Pages/GalleryPage.xaml`, `Views/Pages/SettingsPage.xaml`, DI registration

**Add `GalleryReset` event to the interface:**
```csharp
event EventHandler GalleryReset;
```

**Fire it in `CaptureStorageService.ResetGalleryAsync`** after the reset completes:
```csharp
public async Task ResetGalleryAsync(string capturesDirectory)
{
    // ... existing implementation ...
    GalleryReset?.Invoke(this, EventArgs.Empty);
}
```

**Subscribe in `GalleryViewModel` constructor** (mirror the existing `CaptureSaved` pattern):
```csharp
_captureStorage.GalleryReset += async (_, _) =>
{
    try
    {
        await Application.Current.Dispatcher
            .InvokeAsync(() => LoadAsync()).Task.Unwrap();
        CurrentView = GalleryView.SpeciesList;
    }
    catch (Exception ex) { _logger.LogWarning(ex, "Failed to reload gallery after reset"); }
};
```

**Remove `ResetGalleryCommand` from `GalleryViewModel`** and remove the button from `GalleryPage.xaml` (in the Species List toolbar area, the red "🗑 Reset Gallery" button).

**Add `ICaptureStorageService` to `SettingsViewModel`:**
```csharp
private readonly ICaptureStorageService _captureStorage;

// Add to constructor signature:
public SettingsViewModel(..., ICaptureStorageService captureStorage, ...)
{
    ...
    _captureStorage = captureStorage;
}
```

**Add command:**
```csharp
[RelayCommand]
private async Task ResetGalleryAsync()
{
    if (MessageBox.Show(
            "Delete all captures permanently? This cannot be undone.",
            "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning)
        != MessageBoxResult.Yes) return;

    await _captureStorage.ResetGalleryAsync(_settingsService.CurrentSettings.CapturesDirectory);
}
```

**Add button to `SettingsPage.xaml`** after the Export/Import section, before `<!-- ═══ Point of Interest ═══ -->`:
```xaml
<!-- Reset Gallery -->
<TextBlock Text="Danger Zone" Style="{StaticResource Label}"
           FontWeight="SemiBold" Foreground="#C0392B" Margin="0,12,0,2"/>
<TextBlock Text="Permanently delete all captured photos and detection records. Settings are preserved."
           Style="{StaticResource Hint}"/>
<Button Content="🗑 Reset Gallery"
        Command="{Binding ResetGalleryCommand}"
        Background="#C0392B" Foreground="White"
        BorderThickness="0" Padding="12,6"
        Cursor="Hand" FontSize="12" Margin="0,4,0,0"
        HorizontalAlignment="Left"/>
```

**Update DI registration** (wherever `SettingsViewModel` is registered — likely `App.xaml.cs` or a `ServiceCollectionExtensions` file) to include `ICaptureStorageService` in its constructor parameters. Since it's already registered in the container for `GalleryViewModel`, no new registration is needed — just add the parameter.

**Verify:** Settings page shows "🗑 Reset Gallery" in the Danger Zone under Data & Storage. Clicking it prompts, and on confirm: captures deleted, Gallery page (if navigated to) shows empty species list. Gallery page no longer has a Reset button.
