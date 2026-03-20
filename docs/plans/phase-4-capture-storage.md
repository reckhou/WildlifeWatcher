# Phase 4 — Capture & Storage

**Version:** `v0.3.0` → `v0.4.0`

## Context
Phase 3 detects wildlife in-memory. Phase 4 persists every detection: saves the capture image as JPEG to disk and records it in SQLite. Also adds user-configurable locations for the captures folder and SQLite DB file, with file-migration support when paths change. `settings.json` stays fixed at `%AppData%\WildlifeWatcher\` and acts as the bootstrap for the other configurable paths.

**Decisions:**
- Save trigger: every detection above confidence threshold (subject to cooldown)
- Image format: JPEG quality=85 (converted from PNG)
- Notification: status bar "Saved: Red Fox (92%) — 14:32:05", auto-clears after 5 seconds
- Captures path change: ask user "Move X files?" → move files + update DB paths if confirmed
- DB path change: copy DB file to new path, prompt restart

---

## New Files

| File | Purpose |
|---|---|
| `Services/CaptureStorageService.cs` | Implements `ICaptureStorageService` — PNG→JPEG, file write, DB upsert, migration |

## Modified Files

| File | Change |
|---|---|
| `Models/AppConfiguration.cs` | Add `DatabasePath` (string, default empty → resolves to `%AppData%\WildlifeWatcher\wildlife.db`) |
| `Services/Interfaces/ICaptureStorageService.cs` | Add `event EventHandler<CaptureRecord> CaptureSaved` |
| `Services/RecognitionLoopService.cs` | Inject `ICaptureStorageService`; call `SaveCaptureAsync` on detection |
| `ViewModels/MainViewModel.cs` | Subscribe to `CaptureSaved`; timed status bar via `DispatcherTimer` |
| `ViewModels/SettingsViewModel.cs` | Add `DatabasePath`, browse commands + migration logic |
| `Views/Pages/SettingsPage.xaml` | Add "Data & Storage" section with path fields + browse buttons |
| `App.xaml.cs` | `AddDbContextFactory` (replacing `AddDbContext`); register `ICaptureStorageService` |
| `WildlifeWatcher.csproj` | Add `<UseWindowsForms>true</UseWindowsForms>` for `FolderBrowserDialog` |

---

## Key Implementation Notes

### `AppConfiguration` addition
```csharp
public string DatabasePath { get; set; } = string.Empty;

public string GetEffectiveDatabasePath() =>
    string.IsNullOrWhiteSpace(DatabasePath)
        ? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "WildlifeWatcher", "wildlife.db")
        : DatabasePath;
```

### `App.xaml.cs` — switch to `AddDbContextFactory`
Load settings first to get `DatabasePath`:
```csharp
var bootstrap = new SettingsService(NullLogger<SettingsService>.Instance);
var dbPath = bootstrap.CurrentSettings.GetEffectiveDatabasePath();
services.AddDbContextFactory<WildlifeDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
```
Apply migration at startup via factory:
```csharp
await using var db = await factory.CreateDbContextAsync();
await db.Database.MigrateAsync();
```

### `CaptureStorageService.SaveCaptureAsync`
1. Resolve captures directory (absolute or relative to `%AppData%\WildlifeWatcher\`)
2. PNG → JPEG via `JpegBitmapEncoder { QualityLevel = 85 }` (must run on STA/dispatcher thread)
3. Save file: `{SafeSpeciesName}_{yyyyMMdd_HHmmss}.jpg`
4. Upsert `Species` (find by `CommonName` case-insensitive; create if absent)
5. Create `CaptureRecord`, `db.SaveChangesAsync()`
6. Set `record.Species = species` (populate nav prop)
7. `CaptureSaved?.Invoke(this, record)`

Uses `IDbContextFactory<WildlifeDbContext>` (create+dispose per call — safe for singleton lifetime).

### Other `ICaptureStorageService` methods
```csharp
GetAllSpeciesWithCapturesAsync()
    → db.Species.Include(s => s.Captures).Where(s => s.Captures.Any()).OrderBy(s => s.CommonName)

GetCapturesBySpeciesAsync(int speciesId)
    → db.CaptureRecords.Where(r => r.SpeciesId == speciesId).OrderByDescending(r => r.CapturedAt)

DeleteCaptureAsync(int captureId)
    → find record → File.Delete if exists → db.Remove → SaveChangesAsync

UpdateCaptureNotesAsync(int captureId, string notes)
    → find record → record.Notes = notes → SaveChangesAsync
```

### `RecognitionLoopService` — call save after detection
```csharp
DetectionOccurred?.Invoke(this, ev);
_cooldownUntil = DateTime.UtcNow.AddSeconds(settings.CooldownSeconds);
try { await _captureStorage.SaveCaptureAsync(currentFrame, result); }
catch (Exception ex) { _logger.LogError(ex, "Failed to save capture"); }
```

### `MainViewModel` — status bar notification
```csharp
_captureStorage.CaptureSaved += (_, record) =>
    Application.Current.Dispatcher.Invoke(() => {
        StatusText = $"Saved: {record.Species.CommonName} ({record.ConfidenceScore:P0}) — {record.CapturedAt:HH:mm:ss}";
        _statusClearTimer.Stop(); _statusClearTimer.Start();
    });

_statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
_statusClearTimer.Tick += (_, _) => { StatusText = "Ready"; _statusClearTimer.Stop(); };
```

### Settings — configurable folder locations

**`SettingsPage.xaml` — "Data & Storage" section:**
- `CapturesDirectory`: `TextBox` + "Browse…" button → `FolderBrowserDialog`
- `DatabasePath`: `TextBox` + "Browse…" button → `SaveFileDialog` (*.db)

**`SettingsViewModel.SaveCommand` migration logic:**
```csharp
// If captures path changed and old folder has .jpg files:
//   MessageBox.Show("Move X captures to new folder? [Yes/No]")
//   If Yes: File.Move each file + update ImageFilePath in DB

// If DB path changed:
//   File.Copy(oldDbPath, newDbPath, overwrite: true)
//   Show "Database moved. Please restart the app."
```

`SettingsViewModel` gains `IDbContextFactory<WildlifeDbContext>` for `MigrateCaptures` helper.

---

## Verification

1. `dotnet build` — 0 errors
2. Trigger detection → JPEG in captures folder, DB row with correct `ImageFilePath`
3. Status bar: "Saved: Red Fox (92%) — 14:32:05" for 5s then "Ready"
4. Repeat species → new `CaptureRecord` row, no new `Species` row
5. New species → new `Species` row with `FirstDetectedAt`
6. Move captures: change path → Save → dialog → Yes → files moved, DB paths updated
7. Move DB: change path → Save → DB copied → "Please restart" shown
8. Browse buttons: folder picker for captures; save-file dialog for DB
