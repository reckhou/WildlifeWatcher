# Detection Settings Enhancements

**Goal:** Auto-capture a camera snapshot on window open, persist the POI test interval, and reorder/rename the detection settings sections.

**Architecture:** All changes are confined to `DetectionSettingsWindow.xaml`, `DetectionSettingsViewModel.cs`, `DetectionSettingsWindow.xaml.cs`, and `AppConfiguration.cs`. No new services or DI wiring needed. The section reorder is a pure XAML cut-and-paste. Auto-capture fires on `Window.Loaded` in code-behind. Interval persistence adds one field to the settings model and wires load/save in the ViewModel.

**Tech Stack:** C# 12, WPF, CommunityToolkit.Mvvm, .NET 8

---

## Progress

- [x] Task 1: Persist POI test interval + auto-capture on open
- [ ] Task 2: Reorder and rename sections in XAML

---

## Files

- Modify: `src/WildlifeWatcher/WildlifeWatcher/Models/AppConfiguration.cs` — add `PoiTestIntervalSeconds` field
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/DetectionSettingsViewModel.cs` — load/save interval; add `AutoCaptureZoneBackgroundAsync()` for use on window open
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/DetectionSettingsWindow.xaml.cs` — trigger auto-capture in `Window.Loaded`
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/DetectionSettingsWindow.xaml` — reorder sections and rename "AI Recognition" → "API Settings"

---

### Task 1: Persist POI test interval + auto-capture on open

**Files:**
- `Models/AppConfiguration.cs`
- `ViewModels/DetectionSettingsViewModel.cs`
- `Views/DetectionSettingsWindow.xaml.cs`

**AppConfiguration.cs** — add one property (default 5 to match current VM default):
```csharp
public int PoiTestIntervalSeconds { get; set; } = 5;
```

**DetectionSettingsViewModel.cs:**

1. In `LoadSettings()`, add:
```csharp
ContinuousTestIntervalSeconds = s.PoiTestIntervalSeconds;
```

2. In `AutoSave()`, add:
```csharp
s.PoiTestIntervalSeconds = ContinuousTestIntervalSeconds;
```

3. Wire the existing `OnContinuousTestIntervalSecondsChanged` to also call `AutoSave()` after clamping:
```csharp
partial void OnContinuousTestIntervalSecondsChanged(int value)
{
    if (value < 1)  ContinuousTestIntervalSeconds = 1;
    else if (value > 30) ContinuousTestIntervalSeconds = 30;
    else AutoSave();
}
```
(The clamp branches will re-fire the partial method with the clamped value, so `AutoSave()` runs on the valid path only.)

4. Add a public method for the code-behind to call on window open:
```csharp
public async Task AutoCaptureZoneBackgroundAsync()
{
    if (_camera.IsConnected)
        await CaptureZoneBackgroundAsync();
}
```

**DetectionSettingsWindow.xaml.cs** — in constructor (after DataContext assignment), subscribe to `Loaded`:
```csharp
Loaded += async (_, _) => await viewModel.AutoCaptureZoneBackgroundAsync();
```

**Verify:** Open Detection Settings — the zone canvas should automatically fill with a live camera frame within a second or two (or stay black if camera is not connected). Change interval, close and reopen settings — the saved interval value is restored.

---

### Task 2: Reorder and rename sections in XAML

**Files:** `Views/DetectionSettingsWindow.xaml`

Reorder the `<!-- ═══ … ═══ -->` section blocks from the current order:
```
Capture → Point of Interest → AI Recognition → Daylight Detection → Motion Zones → POI Testing
```
to the target order:
```
POI Testing → Point of Interest → Capture → Motion Zones → Daylight Detection → API Settings
```

Also rename the section header text from `"AI Recognition"` to `"API Settings"`.

No binding changes needed — this is pure XAML block rearrangement.

**Verify:** Open Detection Settings — section order from top to bottom is: POI Testing, Point of Interest, Capture, Motion Zones, Daylight Detection, API Settings. All controls still function (bindings, converters, sliders).
