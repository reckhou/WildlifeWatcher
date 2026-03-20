# Phase 1 — Project Scaffolding & Infrastructure

**Status:** ✅ Complete (`v0.1.0`)

## What was built

- Solution structure: `WildlifeWatcher` (WPF) + `WildlifeWatcher.Tests`
- Dependency injection via `Microsoft.Extensions.Hosting`
- MVVM with `CommunityToolkit.Mvvm`
- SQLite database via EF Core with initial migration (`Species`, `CaptureRecords` tables)
- `SettingsService` — persists `AppConfiguration` to `%AppData%\WildlifeWatcher\settings.json`
- `CredentialService` — stores credentials encrypted via Windows DPAPI
- `MainWindow` shell with dark-green nav sidebar (Live View, Gallery, Settings)
- `MainViewModel` with `CurrentPage` navigation
- Service interfaces defined: `ICameraService`, `IAiRecognitionService`, `ICaptureStorageService`
- Serilog file logging (daily rolling, 30-day retention)

## Key models

- `AppConfiguration` — RTSP URL, cooldown, capture dir, Claude model, frame interval, confidence threshold, AI provider, pre-filter settings
- `Credentials` — RTSP username/password, Anthropic API key
- `Species` — CommonName, ScientificName, Description, FirstDetectedAt
- `CaptureRecord` — SpeciesId, CapturedAt, ImageFilePath, AiRawResponse, ConfidenceScore, Notes
