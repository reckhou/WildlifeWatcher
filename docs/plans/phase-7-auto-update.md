# Phase 7 — Auto-Update

**Version:** `v0.6.0` → `v0.7.0`

## Context
WildlifeWatcher is distributed as a self-contained single-file executable via GitHub Releases (tag `v*.*.*` on `reckhou/WildlifeWatcher`). Users currently have no way to know a newer version exists. This phase adds an auto-update mechanism that checks GitHub Releases on startup, notifies the user via a banner, and applies the update via a PowerShell helper script (since a running Windows executable cannot overwrite itself).

---

## Implementation

### 1. Embed version in the assembly
**File:** `src/WildlifeWatcher/WildlifeWatcher/WildlifeWatcher.csproj`

Add `<Version>0.1.0</Version>` inside the existing `<PropertyGroup>`. Accessible at runtime via `Assembly.GetExecutingAssembly().GetName().Version`.

### 2. Stamp version during CI release build
**File:** `.github/workflows/release.yml`

Add `-p:Version=$VERSION` to the `dotnet publish` command so the published binary's assembly version matches the git tag.

```yaml
dotnet publish ... -p:Version=${{ env.VERSION }}
```

### 3. Create UpdateInfo model
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

### 4. Create IUpdateService
**File:** `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IUpdateService.cs`

```csharp
public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task ApplyUpdateAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default);
}
```

### 5. Create UpdateService
**File:** `src/WildlifeWatcher/WildlifeWatcher/Services/UpdateService.cs`

**CheckForUpdateAsync:**
- GET `https://api.github.com/repos/reckhou/WildlifeWatcher/releases/latest` (`User-Agent: WildlifeWatcher`)
- Parse `tag_name` (strip `v`) → `Version`, find `.zip` asset `browser_download_url`
- Compare with `Assembly.GetExecutingAssembly().GetName().Version`
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

### 6. Register in DI
**File:** `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs`

```csharp
services.AddSingleton<IUpdateService, UpdateService>();
```

### 7. MainViewModel additions
**File:** `src/WildlifeWatcher/WildlifeWatcher/ViewModels/MainViewModel.cs`

- Inject `IUpdateService`
- Observable properties: `bool IsUpdateAvailable`, `string UpdateBannerText` (e.g. "v1.2.0 available"), `bool IsUpdating`, `int UpdateProgress`
- `IAsyncRelayCommand CheckForUpdateCommand` — fire-and-forget on startup
- `IAsyncRelayCommand ApplyUpdateCommand` — download + apply

### 8. Update banner UI
**File:** `src/WildlifeWatcher/WildlifeWatcher/Views/MainWindow.xaml`

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

---

## Files Summary

| File | Action |
|------|--------|
| `Models/UpdateInfo.cs` | New |
| `Services/Interfaces/IUpdateService.cs` | New |
| `Services/UpdateService.cs` | New |
| `WildlifeWatcher.csproj` | Add `<Version>0.1.0</Version>` |
| `.github/workflows/release.yml` | Pass `-p:Version=` to dotnet publish |
| `App.xaml.cs` | Register `IUpdateService` |
| `ViewModels/MainViewModel.cs` | Inject service, add properties + commands |
| `Views/MainWindow.xaml` | Add update banner |

---

## Verification
1. Build — confirm no errors.
2. Temporarily force `CurrentVersion = 0.0.1` in `UpdateService` and call `CheckForUpdateAsync` — should return `IsUpdateAvailable = true`.
3. Confirm banner appears in UI (set via debug/breakpoint).
4. Inspect generated `.ps1` in `%TEMP%` for correct source/dest paths.
5. End-to-end: tag a release, let CI build, install, then release a newer tag — running app detects and applies update.
