# Detection Settings Window Implementation Plan

**Goal:** Move all POI/recognition settings out of the main Settings page into a dedicated modeless `DetectionSettingsWindow`, add instant auto-save for those settings, add a "Test POI" button that runs a full POI capture cycle without calling AI, and remove the debug mode toggle from the nav rail.

**Architecture:** A new `DetectionSettingsViewModel` owns all capture/motion/POI/AI/daylight settings. It auto-saves on each property change by mutating `_settingsService.CurrentSettings` and calling `Save()`. `RecognitionLoopService` gains a `TriggerTestPoiAsync()` method that runs one detection tick in debug mode (frame → background model foreground → POI extraction → save crops, no AI). `MainViewModel` loses `IsDebugMode`; `SettingsViewModel` loses all moved properties. The nav rail gets a "Detection Settings" button that opens/focuses the window.

**Tech Stack:** C# 12, WPF, CommunityToolkit.Mvvm, .NET 8

---

## Progress

- [x] Task 1: Add `TriggerTestPoiAsync()` to recognition loop service
- [x] Task 2: Create `DetectionSettingsViewModel`
- [x] Task 3: Create `DetectionSettingsWindow.xaml` (XAML)
- [x] Task 4: Create `DetectionSettingsWindow.xaml.cs` and register in DI
- [x] Task 5: Strip moved settings from `SettingsPage` and `SettingsViewModel`
- [x] Task 6: Update `MainWindow` and `MainViewModel` — remove debug toggle, add Detection Settings button

---

## Files

- Create: `src/WildlifeWatcher/WildlifeWatcher/Views/DetectionSettingsWindow.xaml` — full detection settings UI
- Create: `src/WildlifeWatcher/WildlifeWatcher/Views/DetectionSettingsWindow.xaml.cs` — code-behind (PasswordBox wiring, motion zone canvas rubber-band)
- Create: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/DetectionSettingsViewModel.cs` — all recognition settings + auto-save + TestPoiCommand
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IRecognitionLoopService.cs` — add `TriggerTestPoiAsync()` and remove `IsDebugMode`
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Services/RecognitionLoopService.cs` — implement `TriggerTestPoiAsync()`, remove `IsDebugMode` property
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/Pages/SettingsPage.xaml` — remove Capture, POI, AI, Daylight, Motion Zones sections
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/SettingsViewModel.cs` — remove all moved properties/commands, fix `SaveAsync` to only save Camera/Location/Data fields
- Modify: `src/WildlifeWatcher/WildlifeWatcher/Views/MainWindow.xaml` — remove Debug Mode toggle, add Detection Settings button
- Modify: `src/WildlifeWatcher/WildlifeWatcher/ViewModels/MainViewModel.cs` — remove `IsDebugMode` and `OnIsDebugModeChanged`
- Modify: `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs` — register `DetectionSettingsViewModel` and `DetectionSettingsWindow` as singletons

---

### Task 1: Add `TriggerTestPoiAsync()` to recognition loop service

**Files:** `Services/Interfaces/IRecognitionLoopService.cs`, `Services/RecognitionLoopService.cs`

Remove `bool IsDebugMode { get; set; }` from the interface and implementation — debug mode is being replaced by the dedicated test button.

Add to the interface:
```csharp
/// <summary>
/// Runs a single POI detection tick (frame extract → background model → POI extraction → save debug crops).
/// Skips AI. Returns a summary string describing what was found and saved.
/// </summary>
Task<string> TriggerTestPoiAsync();
```

Implement in `RecognitionLoopService`:
```csharp
public async Task<string> TriggerTestPoiAsync()
{
    if (!_camera.IsConnected)
        return "Camera not connected.";

    var frame = await _camera.ExtractFrameAsync();
    if (frame == null)
        return "Failed to extract frame.";

    var settings = _settings.CurrentSettings;

    // Update background model with this frame
    _background.ProcessFrame(frame);
    var fg = _background.Foreground;
    var temporalDelta = _background.TemporalDelta;
    if (fg == null)
        return "Background model not ready yet (first frame).";

    var zones = settings.MotionWhitelistZones.Count > 0
        ? (IReadOnlyList<MotionZone>)settings.MotionWhitelistZones : null;

    IReadOnlyList<PoiRegion> regions = Array.Empty<PoiRegion>();
    if (settings.EnablePoiExtraction)
    {
        regions = _poi.ExtractRegions(fg, frame, zones, settings.MotionPixelThreshold,
            settings.PoiSensitivity, temporalDelta, settings.MotionTemporalThreshold,
            settings.MotionTemporalCellFraction);
        PoiRegionsDetected?.Invoke(this, regions);
    }

    if (regions.Count == 0)
        return "POI extraction found 0 regions — nothing to save.";

    await SaveDebugPoiAsync(frame, regions, settings, "Test POI (no AI)", CancellationToken.None);
    return $"Test POI: {regions.Count} region(s) found and saved to captures folder.";
}
```

Also remove the `IsDebugMode` property and the debug-mode branch in `ProcessTickAsync` — the debug branch (lines 169–179 in current file) is deleted entirely.

**Verify:** Code compiles. The `IsDebugMode` property no longer exists on the service.

---

### Task 2: Create `DetectionSettingsViewModel`

**Files:** `ViewModels/DetectionSettingsViewModel.cs`
**Depends on:** Task 1

Full ViewModel holding all moved settings. Uses CommunityToolkit.Mvvm. Auto-save pattern: each `partial void OnXxxChanged(T value)` calls `AutoSave()`. Motion zone operations (AddZone, RemoveZone, ClearZones) also call `AutoSave()`.

Key structure:
```csharp
public partial class DetectionSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService        _settings;
    private readonly ICredentialService      _credentials;
    private readonly ICameraService          _camera;
    private readonly IRecognitionLoopService _recognitionLoop;
    private readonly IBackgroundModelService _backgroundModel;
    private readonly IPointOfInterestService _poi;

    // Capture
    [ObservableProperty] private int    _cooldownSeconds = 30;
    [ObservableProperty] private int    _speciesCooldownMinutes = 5;
    [ObservableProperty] private int    _frameIntervalSeconds = 30;
    [ObservableProperty] private double _minConfidenceThreshold = 0.7;
    [ObservableProperty] private double _motionBackgroundAlpha = 0.05;
    [ObservableProperty] private int    _motionPixelThreshold = 25;
    [ObservableProperty] private int    _motionTemporalThreshold = 8;
    [ObservableProperty] private double _motionTemporalCellFraction = 0.10;

    // POI
    [ObservableProperty] private bool   _enablePoiExtraction = true;
    [ObservableProperty] private bool   _savePoiDebugImages = true;
    [ObservableProperty] private double _poiSensitivity = 0.5;

    // AI
    [ObservableProperty] private AiProvider _aiProvider = AiProvider.Claude;
    [ObservableProperty] private string     _claudeModel = "claude-haiku-4-5-20251001";
    [ObservableProperty] private string     _anthropicApiKey = string.Empty;
    [ObservableProperty] private string     _geminiModel = "gemini-2.0-flash";
    [ObservableProperty] private string     _geminiApiKey = string.Empty;

    // Daylight
    [ObservableProperty] private bool _enableDaylightDetectionOnly;
    [ObservableProperty] private int  _sunriseOffsetMinutes = -30;
    [ObservableProperty] private int  _sunsetOffsetMinutes  = 30;

    // Motion Zones
    [ObservableProperty] private byte[]? _zoneEditorBackground;
    [ObservableProperty] private bool    _isCapturingZoneBackground;
    public ObservableCollection<MotionZoneItem> MotionZones { get; } = new();

    // Test POI
    [ObservableProperty] private string _testPoiStatus = string.Empty;
    [ObservableProperty] private bool   _isTestPoiRunning;

    // Computed advice strings (same as in current SettingsViewModel)
    public string AlphaAdvice => ...;
    public string PoiSensitivityAdvice => ...;
    public string PixelThresholdAdvice => ...;
    public string TemporalThresholdAdvice => ...;
    public bool ShowDaylightLocationWarning => ...;

    // OnXxxChanged triggers AutoSave() and property-change notifications for advice strings

    private void AutoSave()
    {
        var s = _settings.CurrentSettings;
        s.CooldownSeconds                = CooldownSeconds;
        s.SpeciesCooldownMinutes         = SpeciesCooldownMinutes;
        s.FrameExtractionIntervalSeconds = FrameIntervalSeconds;
        s.MinConfidenceThreshold         = MinConfidenceThreshold;
        s.MotionBackgroundAlpha          = MotionBackgroundAlpha;
        s.MotionPixelThreshold           = MotionPixelThreshold;
        s.MotionTemporalThreshold        = MotionTemporalThreshold;
        s.MotionTemporalCellFraction     = MotionTemporalCellFraction;
        s.EnablePoiExtraction            = EnablePoiExtraction;
        s.SavePoiDebugImages             = SavePoiDebugImages;
        s.PoiSensitivity                 = PoiSensitivity;
        s.AiProvider                     = AiProvider;
        s.ClaudeModel                    = ClaudeModel;
        s.GeminiModel                    = GeminiModel;
        s.EnableDaylightDetectionOnly    = EnableDaylightDetectionOnly;
        s.SunriseOffsetMinutes           = SunriseOffsetMinutes;
        s.SunsetOffsetMinutes            = SunsetOffsetMinutes;
        s.MotionWhitelistZones           = new List<MotionZone>(MotionZones.Select(z => z.ToMotionZone()));
        _settings.Save(s);
    }

    public void SaveCredentials()
    {
        _credentials.SaveCredentials(
            _settings.CurrentSettings.RtspUrl is var u ? u : string.Empty, // preserve RTSP creds
            ... // read existing RTSP password from credential store
        );
    }
}
```

For credentials auto-save: wire `AnthropicApiKey` and `GeminiApiKey` setters to call `_credentials.SaveCredentials(...)`. But PasswordBox isn't bound — so credential save happens in code-behind on `PasswordChanged`. The VM exposes `SaveApiCredentials(string anthropicKey, string geminiKey)` which the code-behind calls.

Commands:
- `CaptureZoneBackgroundCommand` — calls `_camera.ExtractFrameAsync()`, sets `ZoneEditorBackground`
- `RemoveZoneCommand(MotionZoneItem)` — removes from list, calls `AutoSave()`
- `ClearZonesCommand` — clears list, calls `AutoSave()`
- `TestPoiCommand` — sets `IsTestPoiRunning = true`, calls `_recognitionLoop.TriggerTestPoiAsync()`, sets `TestPoiStatus` from result

Motion zone management (AddZone called from code-behind):
```csharp
public void AddZone(MotionZone zone)
{
    _settings.CurrentSettings.MotionWhitelistZones.Add(zone);
    RefreshZoneItems();
    AutoSave();
}
```

**Verify:** ViewModel instantiates without error. Properties load from `_settings.CurrentSettings` in constructor.

---

### Task 3: Create `DetectionSettingsWindow.xaml`

**Files:** `Views/DetectionSettingsWindow.xaml`
**Depends on:** Task 2

A `Window` (not `UserControl`) with dark theme matching LiveView (`Background="#1A1A1A"`). Width=640, Height=700, `SizeToContent="Height"` (capped), `ResizeMode="CanResize"`. Title: "Detection Settings".

Define the same styles as SettingsPage (SectionHeader, Label, Field, Hint) as local resources with light background (#F5F5F0) — or keep dark theme throughout. Use light theme for consistency with SettingsPage.

Sections in order:
1. **Capture** — cooldown, species cooldown, frame interval, min confidence, pixel threshold slider, temporal threshold slider, background alpha slider (with advice text for each)
2. **Point of Interest** — EnablePoiExtraction checkbox, SavePoiDebugImages checkbox, PoiSensitivity slider (with advice text)
3. **AI Recognition** — provider ComboBox, Claude section (model + API key PasswordBox), Gemini section (model + API key PasswordBox). Note: "Restart required after changing provider" hint stays.
4. **Daylight Detection** — enable checkbox, location warning border, sunrise/sunset offset boxes
5. **Motion Zones** — snapshot button, 560×315 canvas with zone overlays + rubber-band draw canvas, "Click and drag" hint, Clear All Zones button
6. **Test POI** section — a "Test POI" button, a status TextBlock (binds to `TestPoiStatus`), busy indicator

The Test POI section at the bottom (before a close button):
```xaml
<TextBlock Text="POI Testing" Style="{StaticResource SectionHeader}"/>
<Separator Background="#DDDDDD" Margin="0,0,0,8"/>
<TextBlock Text="Force a single detection cycle (frame capture → motion analysis → POI extraction → save crops). AI is not called." Style="{StaticResource Hint}"/>
<StackPanel Orientation="Horizontal" Margin="0,4,0,4">
    <Button Content="▶ Test POI"
            Command="{Binding TestPoiCommand}"
            Background="#2D5A27" Foreground="White"
            BorderThickness="0" Padding="12,6"
            Cursor="Hand" FontSize="12"/>
    <ProgressBar IsIndeterminate="True" Width="60" Height="8" Margin="8,0,0,0"
                 VerticalAlignment="Center"
                 Visibility="{Binding IsTestPoiRunning, Converter={StaticResource BoolToVisibleConverter}}"/>
</StackPanel>
<TextBlock Text="{Binding TestPoiStatus}"
           FontSize="11" Foreground="#555" Margin="0,2,0,6"
           TextWrapping="Wrap"/>
```

Use converters via `StaticResource` — the window needs the converters from `App.xaml` resources (they're registered globally there; confirm during implementation and add if not).

**Verify:** Window opens, all sections render, sliders and checkboxes are visible and functional.

---

### Task 4: Create `DetectionSettingsWindow.xaml.cs` and register in DI

**Files:** `Views/DetectionSettingsWindow.xaml.cs`, `App.xaml.cs`
**Depends on:** Task 3

Code-behind handles:
1. PasswordBox wiring (same pattern as `SettingsPage.xaml.cs`):
```csharp
public DetectionSettingsWindow(DetectionSettingsViewModel viewModel)
{
    InitializeComponent();
    DataContext = viewModel;

    ApiKeyBox.PasswordChanged += (_, _) =>
        viewModel.SaveApiCredentials(ApiKeyBox.Password, GeminiApiKeyBox.Password);
    GeminiApiKeyBox.PasswordChanged += (_, _) =>
        viewModel.SaveApiCredentials(ApiKeyBox.Password, GeminiApiKeyBox.Password);

    // Pre-populate from loaded credentials
    ApiKeyBox.Password       = viewModel.AnthropicApiKey;
    GeminiApiKeyBox.Password = viewModel.GeminiApiKey;
}
```

2. Motion zone rubber-band drawing — identical to `SettingsPage.xaml.cs` but cast to `DetectionSettingsViewModel`:
```csharp
private void ZoneCanvas_MouseUp(object sender, MouseButtonEventArgs e)
{
    // ... same logic
    ((DetectionSettingsViewModel)DataContext).AddZone(zone);
    // ...
}
```

3. Override `OnClosing` to hide rather than close (so the window can be re-opened without re-creating):
```csharp
protected override void OnClosing(CancelEventArgs e)
{
    e.Cancel = true;
    Hide();
}
```

In `MainWindow.xaml.cs`, add a method (or wire to the button command) to open/focus the detection window.

In `App.xaml.cs`, register:
```csharp
services.AddSingleton<DetectionSettingsViewModel>();
services.AddSingleton<DetectionSettingsWindow>();
```

**Verify:** Window opens from the nav button, closes (hides) on X, re-opens on button click, PasswordBoxes populate correctly.

---

### Task 5: Strip moved settings from `SettingsPage` and `SettingsViewModel`

**Files:** `Views/Pages/SettingsPage.xaml`, `ViewModels/SettingsViewModel.cs`
**Depends on:** Task 2

**SettingsPage.xaml** — remove these entire sections:
- `<!-- ═══ Capture ═══ -->` block (cooldown, species cooldown, frame interval, confidence, pixel/temporal/alpha sliders)
- `<!-- ═══ Point of Interest ═══ -->` block
- `<!-- ═══ AI ═══ -->` block (ComboBox, Claude section, Gemini section)
- `<!-- ═══ Daylight Detection ═══ -->` block
- `<!-- ═══ Motion Zones ═══ -->` block
- Also remove the `xmlns:models` namespace declaration if it's only used by the AI ComboBox items

Keep: Camera, Location, Data & Storage, Updates, Danger Zone, Save button.

**SettingsViewModel.cs** — remove:
- Fields: `CooldownSeconds`, `SpeciesCooldownMinutes`, `FrameIntervalSeconds`, `CapturesDirectory` stays but `MinConfidenceThreshold`, `MotionBackgroundAlpha`, `MotionPixelThreshold`, `MotionTemporalThreshold`, `MotionTemporalCellFraction`, `EnablePoiExtraction`, `SavePoiDebugImages`, `PoiSensitivity`, `AiProvider`, `ClaudeModel`, `AnthropicApiKey`, `GeminiModel`, `GeminiApiKey`, `EnableDaylightDetectionOnly`, `SunriseOffsetMinutes`, `SunsetOffsetMinutes`, `ZoneEditorBackground`, `IsCapturingZoneBackground`
- `MotionZones` collection
- Computed: `AlphaAdvice`, `PoiSensitivityAdvice`, `PixelThresholdAdvice`, `TemporalThresholdAdvice`, `ShowDaylightLocationWarning`
- Changed handlers for those properties
- Commands: `CaptureZoneBackgroundCommand`, `RemoveZoneCommand`, `ClearZonesCommand`
- Methods: `RefreshZoneItems`, `AddZone`
- Zone canvas constants

**SettingsViewModel.SaveAsync** — rewrite to only save Camera/Location/Data fields by mutating `CurrentSettings`:
```csharp
var s = _settingsService.CurrentSettings;
s.RtspUrl = RtspUrl;
s.CapturesDirectory = CapturesDirectory;
s.DatabasePath = DatabasePath;
s.Latitude = _selectedLatitude;
s.Longitude = _selectedLongitude;
s.LocationName = LocationName;
s.DebugForceUpdateAvailable = _debugForceUpdateAvailable;
_settingsService.Save(s);
_credentialService.SaveCredentials(RtspUsername, RtspPassword,
    /* preserve API keys from credential store */);
```

For credentials, `SaveCredentials` needs both RTSP and API keys. Since API keys are no longer in SettingsViewModel, read them from the credential store before saving to preserve them:
```csharp
var existingCreds = _credentialService.LoadCredentials();
_credentialService.SaveCredentials(RtspUsername, RtspPassword,
    existingCreds?.AnthropicApiKey ?? string.Empty,
    existingCreds?.GeminiApiKey ?? string.Empty);
```

Also remove `ApiKeyBox` and `GeminiApiKeyBox` from `SettingsPage.xaml.cs` code-behind.

**Verify:** Settings page compiles, shows only Camera/Location/Data/Updates/DangerZone sections, Save button still works.

---

### Task 6: Update `MainWindow` and `MainViewModel`

**Files:** `Views/MainWindow.xaml`, `Views/MainWindow.xaml.cs`, `ViewModels/MainViewModel.cs`
**Depends on:** Task 4

**MainWindow.xaml:**
- Remove the `DebugToggleStyle` style definition
- Remove the `ToggleButton` ("🐛 Debug Mode") from the nav rail
- Add a `Button` below the Settings nav button:
```xaml
<Button Content="Detection Settings"
        Command="{Binding OpenDetectionSettingsCommand}"
        Style="{StaticResource NavButtonStyle}"/>
```

**MainViewModel.cs:**
- Remove `[ObservableProperty] private bool _isDebugMode`
- Remove `partial void OnIsDebugModeChanged(bool value)`
- Add `IDetectionSettingsWindow` or just inject `DetectionSettingsWindow` directly. Since MainViewModel is a ViewModel it shouldn't reference Views — instead, expose the open command via an event or action.

Clean approach: MainViewModel fires an event, MainWindow subscribes:
```csharp
// MainViewModel.cs
public event Action? OpenDetectionSettingsRequested;

[RelayCommand]
private void OpenDetectionSettings() => OpenDetectionSettingsRequested?.Invoke();
```

In `MainWindow.xaml.cs`, the constructor subscribes:
```csharp
private readonly DetectionSettingsWindow _detectionSettingsWindow;

public MainWindow(MainViewModel viewModel, LiveViewPage liveViewPage,
    SettingsPage settingsPage, GalleryPage galleryPage,
    DetectionSettingsWindow detectionSettingsWindow)
{
    // ...
    _detectionSettingsWindow = detectionSettingsWindow;
    viewModel.OpenDetectionSettingsRequested += OpenDetectionSettings;
}

private void OpenDetectionSettings()
{
    _detectionSettingsWindow.Show();
    _detectionSettingsWindow.Activate();
}
```

**Verify:** Build succeeds. Nav rail shows "Live View", "Gallery", "Settings", "Detection Settings" — no debug toggle. Clicking "Detection Settings" opens the window. Window hides on close and can be re-opened.
