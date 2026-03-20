# WildlifeWatcher — Implementation Plans

## Phases

| Phase | File | Version | Status |
|---|---|---|---|
| 1 | [phase-1-scaffolding.md](phase-1-scaffolding.md) | `v0.1.0` | ✅ Complete |
| 2 | [phase-2-live-camera-settings.md](phase-2-live-camera-settings.md) | `v0.2.0` | 📋 Planned |
| 3 | [phase-3-ai-recognition.md](phase-3-ai-recognition.md) | `v0.3.0` | 📋 Planned |
| 4 | [phase-4-capture-storage.md](phase-4-capture-storage.md) | `v0.4.0` | 📋 Planned |
| 5 | [phase-5-gallery.md](phase-5-gallery.md) | `v0.5.0` | 📋 Planned |
| 6 | [phase-6-weather-calendar.md](phase-6-weather-calendar.md) | `v0.6.0` | 📋 Planned |

## Feature Summary

| Feature | Phase |
|---|---|
| RTSP live camera view (LibVLCSharp) | 2 |
| Settings page (RTSP URL, credentials, capture config) | 2 |
| Motion detection pre-filter (configurable sensitivity) | 3 |
| AI species recognition (Claude vision API) | 3 |
| Background recognition loop with cooldown | 3 |
| Live detection side panel (last 5 detections) | 3 |
| JPEG capture save to disk | 4 |
| SQLite capture records | 4 |
| Configurable captures folder + DB location (with migration) | 4 |
| Status bar save notification | 4 |
| Gallery — species cards grid with search | 5 |
| Gallery — species detail page (capture thumbnails) | 5 |
| Capture detail dialog (full image, notes, delete) | 5 |
| Home location setup (city/postcode via Nominatim) | 6 |
| Weather data paired with captures (Open-Meteo) | 6 |
| Calendar heat-map view (captures by date) | 6 |

## Key Technology Decisions

| Concern | Choice | Reason |
|---|---|---|
| RTSP streaming | LibVLCSharp 3.x | Native WPF `VideoView`, handles both playback and frame extraction |
| AI vision | Claude Haiku via `Anthropic.SDK` | Fast, cheap, good wildlife identification |
| Motion pre-filter | Pixel comparison (grayscale, 160×120) | Zero dependencies, fast, adequate for garden cameras |
| Geocoding | Nominatim (OpenStreetMap) | Free, no API key, handles international city names and postcodes |
| Weather | Open-Meteo | Free, no API key, returns WMO codes + sunrise/sunset |
| Database | SQLite via EF Core | File-based, portable, no server needed |
| Credentials | Windows DPAPI | Encrypted at rest, scoped to current user/machine |
