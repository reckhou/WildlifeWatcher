# Phase 7 — Auto-Update

**Version:** `v1.4.0` → `v1.5.0`

## Context
WildlifeWatcher is distributed as a self-contained single-file executable via GitHub Releases (tag `v*.*.*` on `reckhou/WildlifeWatcher`). Users currently have no way to know a newer version exists. This phase adds an auto-update mechanism that checks GitHub Releases on startup, notifies the user via a banner, and applies the update via a PowerShell helper script (since a running Windows executable cannot overwrite itself).

---

## Implementation

### 1. Create UpdateInfo model
**File:** `src/WildlifeWatcher/WildlifeWatcher/Models/UpdateInfo.cs`

```csharp
public class UpdateInfo
{
    public Version CurrentVersion { get; init; }
    public Version LatestVersion { get; init; }
    public string TagName { get; init; }       // e.g. "v1.2.0"
    public string DownloadUrl { get; init; }   // browser_download_url of the zip asset
    public string ReleaseNotes { get; init; }
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}
```

### 2. Create IUpdateService
**File:** `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IUpdateService.cs`

```csharp
public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task ApplyUpdateAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default);
}
```

### 3. Create UpdateService
**File:** `src/WildlifeWatcher/WildlifeWatcher/Services/UpdateService.cs`

**CheckForUpdateAsync:**
- GET `https://api.github.com/repos/reckhou/WildlifeWatcher/releases/latest` (`User-Agent: WildlifeWatcher`)
- Parse `tag_name` (strip `v`) → `Version`, find `.zip` asset `browser_download_url`
- Compare with `Assembly.GetExecutingAssembly().GetName().Version`
- If `AppConfiguration.DebugForceUpdateAvailable` is `true`, skip the real check and return a fake `UpdateInfo` with `LatestVersion = new Version(99, 0, 0)`
- Return `UpdateInfo` or `null` on any error

**ApplyUpdateAsync:**
- Download zip → `%TEMP%\WildlifeWatcher-update\WildlifeWatcher-update.zip` (stream with byte progress)
- Extract zip → `%TEMP%\WildlifeWatcher-update\extracted\`
- Write PowerShell script to `%TEMP%\WildlifeWatcher-updater.ps1`:
  ```powershell
  Start-Sleep -Seconds 3
  Copy-Item -Path "<extracted_exe>" -Destination "<current_exe>" -Force
  Start-Process "<current_exe>"
  ```
- `Process.Start("powershell.exe", "-ExecutionPolicy Bypass -File \"%TEMP%\\WildlifeWatcher-updater.ps1\"")`
- `Application.Current.Shutdown()`

No extra NuGet packages — uses `System.Net.Http.HttpClient` and `System.IO.Compression.ZipFile`.

### 4. Register in DI
**File:** `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs`

```csharp
services.AddSingleton<IUpdateService, UpdateService>();
```

### 5. MainViewModel additions
**File:** `src/WildlifeWatcher/WildlifeWatcher/ViewModels/MainViewModel.cs`

- Inject `IUpdateService`
- Observable properties: `bool IsUpdateAvailable`, `string UpdateBannerText` (e.g. "v1.2.0 available"), `bool IsUpdating`, `int UpdateProgress`
- `string WindowTitle` property:
  ```csharp
  public string WindowTitle =>
      $"WildlifeWatcher v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
  ```
- `IAsyncRelayCommand CheckForUpdateCommand` — fire-and-forget on startup
- `IAsyncRelayCommand ApplyUpdateCommand` — download + apply

### 6. Update banner UI + window title
**File:** `src/WildlifeWatcher/WildlifeWatcher/Views/MainWindow.xaml`

Bind window title to `WindowTitle`:
```xml
<Window ... Title="{Binding WindowTitle}">
```

Thin banner at top of window, visible only when `IsUpdateAvailable`:

```xml
<Border Visibility="{Binding IsUpdateAvailable, Converter={StaticResource BoolToVis}}"
        Background="#FF8C00" Padding="8,4">
    <DockPanel>
        <Button DockPanel.Dock="Right" Content="Update Now"
                Command="{Binding ApplyUpdateCommand}" Margin="8,0,0,0"/>
        <ProgressBar DockPanel.Dock="Right" Width="120" Minimum="0" Maximum="100"
                     Value="{Binding UpdateProgress}"
                     Visibility="{Binding IsUpdating, Converter={StaticResource BoolToVis}}"/>
        <TextBlock Text="{Binding UpdateBannerText}" VerticalAlignment="Center" Foreground="White"/>
    </DockPanel>
</Border>
```

### App icon
**File:** `src/WildlifeWatcher/WildlifeWatcher/Resources/AppIcon.ico`

Generate a wildlife/camera-themed icon (e.g. camera lens with a bird silhouette, or binoculars) with embedded sizes: 16, 32, 48, 256 px.

Wire it up:
- `WildlifeWatcher.csproj`: add `<ApplicationIcon>Resources\AppIcon.ico</ApplicationIcon>` inside the existing `<PropertyGroup>`
- `Views/MainWindow.xaml`: add `Icon="Resources/AppIcon.ico"` to the `<Window>` element

---

## Files Summary

| File | Action |
|------|--------|
| `Models/UpdateInfo.cs` | New |
| `Models/AppConfiguration.cs` | Add `bool DebugForceUpdateAvailable` (debug option, default `false`) |
| `Services/Interfaces/IUpdateService.cs` | New |
| `Services/UpdateService.cs` | New |
| `App.xaml.cs` | Register `IUpdateService` |
| `ViewModels/MainViewModel.cs` | Inject service; add `WindowTitle`, update properties + commands |
| `Views/MainWindow.xaml` | Bind `Title`; add update banner |
| `Resources/AppIcon.ico` | New — wildlife/camera themed icon (16/32/48/256 px) |
| `WildlifeWatcher.csproj` | Set `<ApplicationIcon>Resources\AppIcon.ico</ApplicationIcon>` |

---

## Verification
1. Build — confirm no errors.
2. **Debug testing:** Set `DebugForceUpdateAvailable = true` in `AppConfiguration` (or in Settings if exposed) → launch app → update banner should appear immediately without hitting GitHub API. Reset to `false` when done.
3. Confirm banner appears in UI with correct version text.
4. Inspect generated `.ps1` in `%TEMP%` for correct source/dest paths.
5. Confirm window title shows current assembly version (e.g. "WildlifeWatcher v1.5.0").
6. Push a `v1.5.0` tag → confirm CI release workflow produces a `.exe` with `FileVersion = 1.5.0`.
