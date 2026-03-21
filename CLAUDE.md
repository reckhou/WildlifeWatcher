# Wildlife Watcher

## Project Overview
A garden wild life watcher APP based on C# .NET WPF framework. Connects to camera through RTSP protocol and uses AI to recognize wildlife appeared.

## Global Rules

This project follows the global Claude Code rules defined in [ShanesClaudeCodeGlobalRules](https://github.com/reckhou/ShanesClaudeCodeGlobalRules):
- **Preferences & Workflows**: See `preferences.md` (plans, docs, communication style)
- **Git & Versioning**: See `git-workflow.md` (commit conventions, version format, CI/CD)
- **Coding Standards**: See `coding-standards.md`
- **Security**: See `security.md`

## Project-Specific Versioning

**Version source of truth**: `<Version>` in `src/WildlifeWatcher/WildlifeWatcher/WildlifeWatcher.csproj`

Update the version number in the `.csproj` file as part of every version commit.

## Design Principles

- Allow to watch the connected camera video in real time.
- Captures the photo and save to local if a new species is detected.
- Allow to browse the captures and groups by species.
- Set a cooldown between two captures to avoid overloading AI API calling.