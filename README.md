# Wildlife Watcher

A garden wildlife watcher application built with C# .NET WPF that monitors live camera feeds via RTSP protocol and uses AI-powered computer vision to detect and identify wildlife species in real time.

## Features

- **Live Video Monitoring** — Stream and watch connected RTSP camera feeds with real-time display
- **AI Wildlife Detection** — Automatic species recognition using Claude or Google Gemini AI models
- **Auto-Capture** — Captures photos when wildlife is detected with configurable cooldown between captures
- **Species Gallery** — Browse and organize captured wildlife photos grouped by species
- **Weather & Location** — Display current weather conditions and location information
- **Calendar View** — Track wildlife sightings over time with calendar integration
- **Local Storage** — Save captures and database locally for privacy and offline access
- **Auto-Update** — Built-in self-update mechanism to stay current with latest features and fixes
- **Data Export/Import** — Backup and restore settings, captures, and wildlife database

## Requirements

- Windows 10/11 (x64)
- RTSP-compatible IP camera
- Internet connection (for AI recognition)
- Claude or Google Gemini API key

## Getting Started

1. Download the latest release from [Releases](https://github.com/reckhou/WildlifeWatcher/releases)
2. Extract and run `WildlifeWatcher.exe`
3. Configure your camera's RTSP URL and credentials
4. Add your AI provider API key (Claude or Gemini)
5. Adjust detection sensitivity and capture cooldown settings
6. Start monitoring!

## Configuration

All settings are managed through the app's Settings interface:
- **Camera** — RTSP URL, username, and password
- **AI Provider** — Choose Claude or Gemini and select your preferred model
- **Capture** — Cooldown interval, sensitivity thresholds
- **Storage** — Custom paths for captures and database
- **Location** — Set your garden's location for weather and timezone
- **Debug** — Optional debug settings and forced update testing

## License

This project is released under the **Do What The F*ck You Want To Public License** (WTFPL).

```
DO WHAT THE F*CK YOU WANT TO PUBLIC LICENSE
Version 2, December 2004

Copyright (C) 2004 Sam Hocevar <sam@hocevar.net>

Everyone is permitted to copy and distribute verbatim or modified
copies of this license document, and changing it is allowed as long
as the name is changed.

DO WHAT THE F*CK YOU WANT TO PUBLIC LICENSE
TERMS AND CONDITIONS FOR COPYING, DISTRIBUTION AND MODIFICATION

0. You just DO WHAT THE F*CK YOU WANT TO.
```

See [WTFPL](http://www.wtfpl.net/) for details.

## Development

Built with:
- **.NET 8.0** — Latest LTS framework
- **WPF** — Desktop UI framework
- **Entity Framework Core** — SQLite database ORM
- **LibVLCSharp** — RTSP streaming
- **Anthropic SDK** — Claude AI integration
- **Generative AI SDK** — Google Gemini integration
- **Community Toolkit MVVM** — Modern WPF patterns

## Contributing

Feel free to fork, modify, and distribute under the WTFPL license.
