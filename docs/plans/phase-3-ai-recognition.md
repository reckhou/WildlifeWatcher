# Phase 3 — AI Wildlife Recognition Engine

**Version:** `v0.2.0` → `v0.3.0`

## Context
Adds the AI recognition pipeline: a motion-detection pre-filter with configurable sensitivity, a Claude vision service, and a background recognition loop. Results shown in a side panel on the LiveView page as an in-memory session history (no persistence — Phase 4 handles that).

**Decisions:**
- Pre-filter: motion detection (pixel comparison) with configurable `MotionSensitivity` (0.0–1.0)
- AI provider: Claude only (`claude-haiku-4-5-20251001`) via `Anthropic.SDK`
- LiveView UI: side panel showing last 5 detections (species, confidence, time, thumbnail)

---

## New Files

| File | Purpose |
|---|---|
| `Models/DetectionEvent.cs` | In-memory record: `DetectedAt`, `Result`, `FramePng` |
| `Services/Interfaces/IMotionDetectionService.cs` | `HasMotion(prev, curr, sensitivity) → bool` |
| `Services/Interfaces/IRecognitionLoopService.cs` | `IsRunning`, `IsAnalyzing`, `DetectionOccurred`, `IsAnalyzingChanged` |
| `Services/MotionDetectionService.cs` | Pixel-comparison motion detector |
| `Services/ClaudeRecognitionService.cs` | `IAiRecognitionService` via Anthropic.SDK |
| `Services/RecognitionLoopService.cs` | `IHostedService` background loop |
| `Converters/BytesToBitmapConverter.cs` | `byte[]` → `BitmapSource` for thumbnail binding |

## Modified Files

| File | Change |
|---|---|
| `WildlifeWatcher.csproj` | Add `Anthropic.SDK` NuGet package |
| `Models/AppConfiguration.cs` | Add `MotionSensitivity` (double, default `0.5`) |
| `ViewModels/LiveViewModel.cs` | Add `RecentDetections`, `IsAnalyzing`, `LastDetectionText`; inject `IRecognitionLoopService` |
| `ViewModels/SettingsViewModel.cs` | Add `MotionSensitivity` property |
| `Views/Pages/LiveViewPage.xaml` | Add detection side panel (3-column layout) |
| `Views/Pages/SettingsPage.xaml` | Add Motion Sensitivity slider to Capture section |
| `App.xaml.cs` | Register 3 new services + hosted service cast pattern |
| `App.xaml` | Register `BytesToBitmapConverter` |

---

## Key Implementation Notes

### `AppConfiguration` addition
```csharp
public double MotionSensitivity { get; set; } = 0.5;
```

### `MotionDetectionService`
1. Decode each PNG → `FormatConvertedBitmap` (Gray8) → scale to 160×120 → `.Freeze()`
2. `CopyPixels` to `byte[19200]`
3. Count pixels where grayscale delta > 25
4. `triggerFraction = (1.0 - sensitivity) * 0.15`
   - sensitivity=1.0 → triggers on any change; sensitivity=0.0 → needs 15% pixels changed
5. Returns `true` if `changedFraction >= triggerFraction`

**Note:** Freeze bitmaps before crossing to background thread (WPF STA requirement).

### `ClaudeRecognitionService`
- NuGet: `<PackageReference Include="Anthropic.SDK" Version="4.*" />`
- Returns `RecognitionResult(false,…)` immediately if no API key (log warning, no exception)
- **System prompt:** ask for strict JSON only — no markdown fences:
  ```
  {"detected":true,"common_name":"Red Fox","scientific_name":"Vulpes vulpes","confidence":0.92,"description":"..."}
  ```
- **User message:** base64 PNG image block + `"Identify any wild animals in this image."`
- `MaxTokens = 512`
- Parse with `System.Text.Json.JsonDocument`; strip accidental markdown fences defensively

### `RecognitionLoopService` (IHostedService)

**Interface:**
```csharp
public interface IRecognitionLoopService {
    bool IsRunning { get; }
    bool IsAnalyzing { get; }
    event EventHandler<DetectionEvent> DetectionOccurred;
    event EventHandler<bool> IsAnalyzingChanged;
}
```

**Each tick (`ProcessTickAsync`):**
1. Skip if `!_camera.IsConnected`
2. `currentFrame = await _camera.ExtractFrameAsync()` → skip if null
3. If `EnableLocalPreFilter && _previousFrame != null`: `HasMotion(...)` → skip AI if no motion
4. If `DateTime.UtcNow < _cooldownUntil` → skip AI
5. `IsAnalyzingChanged?.Invoke(this, true)`
6. `result = await _ai.RecognizeAsync(currentFrame, ct)`
7. `IsAnalyzingChanged?.Invoke(this, false)`
8. If detected + confidence ≥ threshold: fire `DetectionOccurred`, set `_cooldownUntil`
9. `_previousFrame = currentFrame`

Uses `PeriodicTimer` — naturally sequential, no overlapping ticks.
Cooldown skips only the AI call — frame extraction and motion tracking continue.

### DI registration (hosted service cast pattern)
```csharp
services.AddSingleton<IRecognitionLoopService, RecognitionLoopService>();
services.AddHostedService(sp =>
    (RecognitionLoopService)sp.GetRequiredService<IRecognitionLoopService>());
```

### `LiveViewModel` additions
```csharp
// On DetectionOccurred event (from background thread):
Application.Current.Dispatcher.Invoke(() => {
    RecentDetections.Insert(0, e);
    while (RecentDetections.Count > 5) RecentDetections.RemoveAt(5);
});
```

### `LiveViewPage.xaml` — 3-column layout (avoids LibVLC HWND airspace issue)
```
Grid (cols: *, 5, 250)
  Col 0: existing video + status bar
  Col 1: GridSplitter
  Col 2: Detection panel
    Header "Recent Detections"
    ProgressBar + "Analyzing..." (visible when IsAnalyzing)
    "No detections yet." (DataTrigger on Count==0)
    ScrollViewer > ItemsControl:
      thumbnail Image (BytesToBitmapConverter on FramePng)
      CommonName, ScientificName, Confidence badge, timestamp
```

### `SettingsPage.xaml` — Motion Sensitivity slider
```xml
<Slider Value="{Binding MotionSensitivity}" Minimum="0" Maximum="1"
        TickFrequency="0.1" IsSnapToTickEnabled="True"/>
```

### `BytesToBitmapConverter`
```csharp
var bitmap = BitmapFrame.Create(new MemoryStream(bytes),
    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
bitmap.Freeze();
return bitmap;
```

---

## Implementation Order
1. `AppConfiguration` + `DetectionEvent`
2. `IMotionDetectionService` + `MotionDetectionService`
3. `Anthropic.SDK` NuGet + `ClaudeRecognitionService`
4. `IRecognitionLoopService` + `RecognitionLoopService`
5. `BytesToBitmapConverter`
6. `LiveViewModel` + `SettingsViewModel` updates
7. `LiveViewPage.xaml` + `SettingsPage.xaml` updates
8. `App.xaml.cs` + `App.xaml` wiring

---

## Verification

1. `dotnet build` — 0 errors
2. No camera/API key → LiveView shows "No detections yet."; logs show "No API key" warning
3. Camera + API key: set sensitivity=0.8, interval=10s, cooldown=30s → wave hand → "Analyzing..." spinner → detection card appears
4. After 6+ detections → only 5 cards shown (trim confirmed)
5. Motion Sensitivity slider saves/loads correctly from settings.json
