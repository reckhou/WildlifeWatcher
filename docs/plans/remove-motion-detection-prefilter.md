# Plan: Remove Motion Detection Pre-filter

## Analysis: Is Motion Detection Redundant?

**Short answer: Yes — it can be safely removed.**

### How the two steps work

**Motion detection (`MotionDetectionService.HasMotion`)**:
- Scans the EMA foreground array (`fg`) globally
- Counts pixels where `fg[i] > pixelThreshold`
- Triggers if that fraction ≥ `(1.0 - sensitivity) × 0.08`
- No frame decode; purely an O(n) array scan

**POI detection (`PointOfInterestService.ExtractRegions`)**:
- Also scans the same `fg` array, same `pixelThreshold`, same O(n) cost
- Groups hot pixels into 5×5 cells, then BFS to find connected components
- **Returns empty early (line 112–113), before any `SKBitmap.Decode`, if no components found**
- Only decodes the full-res frame when at least one cluster exists

### Where the overlap is

Both operate on the identical `fg` foreground array with the identical `pixelThreshold`.
If motion detection says "nothing moved", POI's hot-cell grid would also be empty → zero
components → early return → AI skipped. The paths converge on the same outcome.

The only scenario where they diverge: diffuse scattered noise produces enough changed pixels
to trip the global fraction check, but no clusters form. In that case motion detection
gates correctly — but POI also returns 0 regions, and `RecognitionLoopService` line 146–150
already gates on `poiRegions.Count == 0`. So the AI is still skipped either way.

**The motion pre-filter only saves the cost of POI's O(n) grid scan + BFS — trivial work
that is cheaper than the motion detection scan itself.**

### Conclusion

The motion pre-filter is redundant. The existing POI zero-region gate is sufficient.
Removing it simplifies the pipeline and eliminates a confusingly overlapping concept.

---

## What to remove / keep

| Item | Action | Reason |
|---|---|---|
| `MotionDetectionService.cs` | **Delete** | No longer needed |
| `IMotionDetectionService.cs` | **Delete** | No longer needed |
| `EnableLocalPreFilter` (AppConfiguration) | **Delete** | Controls removed feature |
| `MotionSensitivity` (AppConfiguration) | **Delete** | Only used by motion detection |
| `MotionPixelThreshold` (AppConfiguration) | **Keep** | Still used by POI extraction |
| `IMotionDetectionService` DI registration (App.xaml.cs) | **Remove** | |
| `_motion` field + ctor param in RecognitionLoopService | **Remove** | |
| Motion pre-filter block (RecognitionLoopService lines 127–136) | **Remove** | |
| `MotionSensitivity` in SettingsViewModel (property, load, save, advice) | **Remove** | |
| `EnableLocalPreFilter` in SettingsViewModel | **Remove** | |
| `SensitivityAdvice` computed property in SettingsViewModel | **Remove** | Only describes motion sensitivity |
| `OnMotionSensitivityChanged` partial in SettingsViewModel | **Remove** | |
| Motion sensitivity UI controls in Settings XAML | **Remove** | |
| Unit test asserting `EnableLocalPreFilter == true` | **Update** | Remove that assertion |
| `docs/recognition-pipeline.md` | **Update** | Remove motion pre-filter section |

---

## Files to modify

1. `src/WildlifeWatcher/WildlifeWatcher/Services/RecognitionLoopService.cs` — remove `_motion` field, ctor param, pre-filter block
2. `src/WildlifeWatcher/WildlifeWatcher/Models/AppConfiguration.cs` — remove `EnableLocalPreFilter`, `MotionSensitivity`
3. `src/WildlifeWatcher/WildlifeWatcher/ViewModels/SettingsViewModel.cs` — remove motion sensitivity property, advice, load/save references
4. `src/WildlifeWatcher/WildlifeWatcher/App.xaml.cs` — remove DI registration
5. `src/WildlifeWatcher/WildlifeWatcher.Tests/UnitTest1.cs` — remove `EnableLocalPreFilter` assertion
6. `docs/recognition-pipeline.md` — update to reflect simplified pipeline

## Files to delete

- `src/WildlifeWatcher/WildlifeWatcher/Services/MotionDetectionService.cs`
- `src/WildlifeWatcher/WildlifeWatcher/Services/Interfaces/IMotionDetectionService.cs`

## Settings XAML

Need to locate and remove the motion sensitivity slider and enable-pre-filter toggle from the Settings page XAML (file TBD — need to find during implementation).

---

## Verification

1. Build succeeds with no compile errors
2. App starts, connects to camera, processes frames without the motion pre-filter step
3. POI extraction still gates correctly (zero-region → skip AI)
4. `MotionPixelThreshold` setting still works as POI pixel sensitivity
5. Settings page no longer shows motion sensitivity slider
