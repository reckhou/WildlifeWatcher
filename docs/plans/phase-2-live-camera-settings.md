# Phase 2 — Live Camera View & Settings Page

**Version:** `v0.1.0` → `v0.2.0`

## Context
Implements the first two functional pages: a live RTSP camera view and a Settings page. LibVLCSharp handles both live playback and frame extraction (needed by Phase 3 AI). Settings page is built now so the user can configure the RTSP URL and credentials before connecting.

---

## New Files

| File | Purpose |
|---|---|
| `Services/RtspCameraService.cs` | `ICameraService` implementation using LibVLC |
| `ViewModels/LiveViewModel.cs` | ViewModel for live view page |
| `ViewModels/SettingsViewModel.cs` | ViewModel for settings form |
| `Views/Pages/LiveViewPage.xaml` + `.cs` | `VideoView` + status bar |
| `Views/Pages/SettingsPage.xaml` + `.cs` | Scrollable settings form |
| `Views/Pages/GalleryPlaceholderPage.xaml` + `.cs` | Placeholder for Gallery nav |
| `Converters/BoolToCollapsedConverter.cs` | `true` → `Collapsed` |

## Modified Files

| File | Change |
|---|---|
| `WildlifeWatcher.csproj` | Add `LibVLCSharp`, `LibVLCSharp.WPF`, `VideoLAN.LibVLC.Windows` |
| `Services/Interfaces/ICameraService.cs` | Add `MediaPlayer MediaPlayer { get; }` property |
| `App.xaml.cs` | Register `ICameraService`, `LiveViewModel`, `SettingsViewModel`, page singletons |
| `App.xaml` | Add `BoolToCollapsedConverter` to `Application.Resources` |
| `Views/MainWindow.xaml` | Replace placeholder with `<ContentControl x:Name="PageContentControl">` |
| `Views/MainWindow.xaml.cs` | Inject pages; implement `UpdateContentArea` switch |

---

## Key Implementation Notes

### NuGet Packages
```xml
<PackageReference Include="LibVLCSharp" Version="3.*" />
<PackageReference Include="LibVLCSharp.WPF" Version="3.*" />
<PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.*" />
```
`VideoLAN.LibVLC.Windows` auto-copies `libvlc.dll` / `libvlccore.dll` to output folder.

### `RtspCameraService`
- Constructor: `Core.Initialize()` → `new LibVLC()` → `new MediaPlayer(_libVlc)` (all singleton fields)
- `ConnectAsync()`: build `rtsp://user:pass@host/path` URL (percent-encode credentials), create `Media`, call `_mediaPlayer.Play()`
- `DisconnectAsync()`: `_mediaPlayer.Stop()`
- `TestConnectionAsync()`: temporary MediaPlayer, race `Playing` event vs 8-second timeout
- `ExtractFrameAsync()`: `await Task.Run(() => _mediaPlayer.TakeSnapshot(0, tempFile, 0, 0))` → read bytes → delete temp
- Events: `_mediaPlayer.Playing` → `ConnectionStateChanged(true)`; `Stopped/EncounteredError` → `ConnectionStateChanged(false)`
- LibVLC fires events from background thread — callers must marshal to UI dispatcher

### `LiveViewModel`
- Properties: `IsConnected`, `StatusText`, `ConnectionButtonText`, `MediaPlayer`
- `ToggleConnectionCommand`: connect/disconnect based on current state
- `ConnectionStateChanged` handler: `App.Current.Dispatcher.Invoke(...)` to update properties

### `LiveViewPage.xaml` — layout (2 rows, avoids LibVLC airspace issue)
```
Grid
  Row 0 (*):   vlc:VideoView MediaPlayer="{Binding MediaPlayer}"
               "No camera connected" placeholder (BoolToCollapsedConverter on IsConnected)
  Row 1 (Auto): Status bar (dark green #1E2A1E)
                  Status dot + StatusText + Connect/Disconnect button
```
**Do NOT `Visibility.Collapse` the `VideoView`** — destroys the native HWND. Pages are singletons.

### `SettingsPage.xaml` — three sections
- **Camera**: RTSP URL, Username, Password (PasswordBox), Test Connection button
- **Capture**: Cooldown, Frame interval, Captures directory, Min confidence
- **AI**: Provider ComboBox, Claude model, Anthropic API key (PasswordBox)

`PasswordBox` does not support data binding — wire in code-behind:
```csharp
RtspPasswordBox.PasswordChanged += (_, _) => ViewModel.RtspPassword = RtspPasswordBox.Password;
```

### Navigation (`MainWindow.xaml.cs`)
```csharp
PageContentControl.Content = page switch {
    AppPage.LiveView => (object)LiveViewPage,
    AppPage.Settings => SettingsPage,
    _                => GalleryPlaceholderPage
};
```
Pages are `AddSingleton` — `VideoView` binds once and keeps rendering on navigation.

---

## Verification

1. `dotnet restore` — `libvlc.dll` + `libvlccore.dll` appear in `bin/Debug/`
2. `dotnet build` — 0 errors
3. Launch → LiveView shows black background + "No camera connected" placeholder
4. Navigate to Settings → form renders with all sections
5. Enter dummy values → Save → "Settings saved." — no exception
6. Navigate back to LiveView → no crash (VideoView singleton reuse confirmed)
7. With real RTSP camera: Connect → "Connected", video appears; Disconnect → placeholder returns
