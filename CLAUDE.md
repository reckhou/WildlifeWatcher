# Wildlife Watcher

## Project Overview
A garden wild life watcher APP based on C# .NET WPF framework. Connects to camera through RTSP protocol and uses AI to recognize wildlife appeared.

## Development Guidelines

## Global
- Do not auto commit unless explicitly told to do so.
- When initializing, create a private Github repository. Create automated build and release actions.

## Versioning
- Version format: `vMAJOR.MINOR.PATCH` (e.g. `v1.0.0`)
- The source of truth is `config/version` in `project.godot` — update it as part of every commit
- If no version is specified when committing, ask whether it is a major, minor, or bug fix release:
  - **Major**: +1 to 1st digit, reset 2nd and 3rd to 0 (e.g. `v1.2.3` → `v2.0.0`)
  - **Minor**: +1 to 2nd digit, reset 3rd to 0 (e.g. `v1.2.3` → `v1.3.0`)
  - **Bug fix**: +1 to 3rd digit (e.g. `v1.2.3` → `v1.2.4`)
- After committing, always create and push a matching git tag (`git tag vX.Y.Z && git push origin vX.Y.Z`) — this triggers the CI build-release workflow

## Design Principles

- Allow to watch the connected camera video in real time.
- Captures the photo and save to local if a new species is detected
- Allow to browse the captures and groups by species.
- Set a cooldown between two captures to avoid overloading AI API calling.