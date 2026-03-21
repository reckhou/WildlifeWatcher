# Plan: Fix Force Reinstall UI Freeze + Add Startup Splash Screen

## Context

Two UX issues to fix:
1. **Force reinstall freezes at 100%**: `ZipFile.ExtractToDirectory()` in `UpdateService.ApplyUpdateAsync` is synchronous — it blocks the UI thread after download completes, causing the window to hang with no feedback.
2. **Slow startup with no feedback**: `App.OnStartup` does all heavy work synchronously (DI host build, EF migrations, species merge) before showing any UI, causing a blank period on launch.

---

## Fix 1 — Non-blocking extraction with progress

**Root cause** (`src/WildlifeWatcher/WildlifeWatcher/Services/UpdateService.cs` line 151):
```csharp
ZipFile.ExtractToDirectory(zipPath, extractDir);  // synchronous, blocks UI thread
progress.Report(100);
```

**Fix**: Split progress into download phase (0–90%) and extraction phase (90–100%). Extract entries one-by-one on a background thread via `Task.Run`, reporting progress per entry.

**Files to modify:**
- `src/WildlifeWatcher/WildlifeWatcher/Services/UpdateService.cs`
  - Scale download progress to 0–90% range
  - Replace `ZipFile.ExtractToDirectory` with `Task.Run` + entry-by-entry extraction reporting 90–100%

---

## Fix 2 — Startup splash screen

**Root cause** (`App.xaml.cs` `OnStartup`): All heavy work runs synchronously before `mainWindow.Show()`:
- Serilog config
- `_host.Build()` + `_host.Start()`
- `db.Database.Migrate()`
- `captureStorage.MergeSpeciesByScientificNameAsync().GetAwaiter().GetResult()`

**Approach**: Show a `SplashWindow` immediately, move heavy work to `Task.Run` / async, then close splash and show `MainWindow`.

### New file: `SplashWindow.xaml`
Simple borderless window, dark green theme (matching the app), centered on screen:
- App name "Wildlife Watcher" in large text
- "Loading…" status text
- No title bar, no resize handles, ~400×220

### Changes to `App.xaml.cs` `OnStartup`:
- Show `SplashWindow` before any heavy work
- Move host build, migrations, and species merge to `Task.Run`
- Show `MainWindow` and close splash when done
- Use `Dispatcher.InvokeAsync(async () => ...)` pattern to enable async from the synchronous `OnStartup`

**Files to create:**
- `src/WildlifeWatcher/WildlifeWatcher/Views/SplashWindow.xaml`
- `src/WildlifeWatcher/WildlifeWatcher/Views/SplashWindow.xaml.cs`

**Files to modify:**
- `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs`

---

## Verification

1. **Fix 1**: Trigger force reinstall → progress moves 0→90% (download), 90→100% (extraction), window stays responsive
2. **Fix 2**: Launch app → splash appears immediately → disappears when main window is ready
