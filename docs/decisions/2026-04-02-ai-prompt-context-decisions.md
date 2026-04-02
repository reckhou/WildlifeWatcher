# Decision Log: AI Prompt Context Fields

## Brainstorming Phase — 2026-04-02

### Decision: Scope — Foundation
- **Chosen:** Foundation
- **Alternatives considered:** Throwaway spike
- **Rationale:** Touches `AppConfiguration` (persisted data), both recognition services, and `DetectionSettingsWindow` UI
- **Trade-offs accepted:** Requires plan before coding

### Decision: Prompt structure — Structured fields (Option B)
- **Chosen:** Three slots: habitat description, location (auto from `LocationName`), species hint
- **Alternatives considered:**
  - Option A: single free-text `AiContextDescription` field
  - Option C: template with `{location}` placeholder
  - Option D: auto-inject location only, no user text
- **Rationale:** Structured fields give user control without risk of breaking the JSON output contract; each slot maps to a specific sentence in the prompt
- **Trade-offs accepted:** Slightly more fields than Option A, but avoids template syntax complexity

### Decision: New AppConfiguration fields
- **Chosen:** `AiHabitatDescription` (default: `"a garden"`) and `AiTargetSpeciesHint` (default: `"Wildlife, particularly birds"`)
- **Alternatives considered:** Single combined field
- **Rationale:** Separate fields allow independent hiding of the species hint line when blank
- **Trade-offs accepted:** Two fields instead of one

### Decision: Location field
- **Chosen:** Re-use existing `LocationName` from `AppConfiguration` — no new field
- **Alternatives considered:** New `AiLocationContext` field, embed lat/lon coordinates
- **Rationale:** `LocationName` is already user-set via Settings page; adding a duplicate field creates sync issues
- **Trade-offs accepted:** Location text quality depends on what the user typed in the Settings page

### Decision: Field visibility rules
- **Chosen:** `AiHabitatDescription` is required/non-blank (validated, default kept if cleared). Location line hidden if `LocationName` is blank. Species hint line hidden if `AiTargetSpeciesHint` is blank.
- **Rationale:** Matches user spec exactly
- **Trade-offs accepted:** None

### Decision: Prompt preview in UI
- **Chosen:** Show the first 4 assembled prompt lines as read-only text in DetectionSettingsWindow (AI section). JSON format instructions hidden.
- **Rationale:** Gives user confidence the prompt is correct without overwhelming them with the full system prompt
- **Trade-offs accepted:** Preview is informational only — not editable directly

### Decision: UI placement
- **Chosen:** New "AI Prompt" sub-section inside the existing AI Recognition section of `DetectionSettingsWindow`
- **Alternatives considered:** Separate "Prompt" tab
- **Rationale:** Keeps all AI config in one place; no need for a tab at this scope
- **Trade-offs accepted:** AI section grows slightly longer

## Planning Phase — 2026-04-02

### Decision: Prompt assembly — static PromptBuilder class
- **Chosen:** New `Services/PromptBuilder.cs` with `Build(AppConfiguration)` and `BuildPreview(...)` static methods
- **Alternatives considered:** Method on `AppConfiguration` itself; duplicating logic in each recognition service
- **Rationale:** Single source of truth for prompt structure; both recognition services call the same builder; VM preview calls `BuildPreview` without needing a full `AppConfiguration` instance
- **Trade-offs accepted:** One extra file; `BuildPreview` duplicates some conditional logic from `Build`

### Decision: PromptPreview dependency on LocationName
- **Chosen:** `PromptPreview` reads `_settings.CurrentSettings.LocationName` directly; `OnPropertyChanged(nameof(PromptPreview))` raised at constructor end
- **Alternatives considered:** Make `LocationName` an observable property on `DetectionSettingsViewModel`
- **Rationale:** LocationName is owned by `SettingsViewModel`/Settings page — duplicating it in `DetectionSettingsViewModel` creates a sync problem. Reading from settings at render time is simpler and correct for this read-only preview
- **Trade-offs accepted:** Preview doesn't live-update if the user changes LocationName while DetectionSettingsWindow is open (they'd need to close and reopen); acceptable given the rare edit frequency
