# Plan: Clickable Recent Detections → CaptureDetailDialog

## Context

The Recent Detections panel in the Live View shows up to 5 of the latest detection events (image thumbnail, species name, confidence, timestamp). Currently these cards are display-only — clicking does nothing. The goal is to make each card clickable so it opens the existing `CaptureDetailDialog` for the corresponding saved capture.

## Approach

When a detection event is clicked, query `ICaptureStorageService.GetCapturesByDateAsync(e.DetectedAt.Date)` to retrieve all captures saved that day, then find the one matching `e.Result.CommonName` closest in time to `e.DetectedAt`. If a match is found, open `CaptureDetailDialog`. If no match (detection skipped due to cooldown), silently do nothing — no error dialog.

This avoids any model changes and follows the existing `GalleryViewModel.OpenCapture` pattern exactly.

## Files to Modify

### 1. `ViewModels/LiveViewModel.cs`

- Add `ICaptureStorageService _captureStorage` constructor parameter and field
- Add `[RelayCommand]` method:

```csharp
[RelayCommand]
private async Task OpenDetectionAsync(DetectionEvent e)
{
    var dayCaptures = await _captureStorage.GetCapturesByDateAsync(e.DetectedAt.Date);
    var match = dayCaptures
        .Where(c => c.Species.CommonName == e.Result.CommonName)
        .MinBy(c => Math.Abs((c.CapturedAt - e.DetectedAt).Ticks));
    if (match is null) return;

    var dialog = new CaptureDetailDialog(match, _captureStorage);
    dialog.ShowDialog();
}
```

- Add `using WildlifeWatcher.Views.Dialogs;` import

### 2. `Views/Pages/LiveViewPage.xaml`

Add `Cursor="Hand"` and `InputBindings` to the detection card `Border` (lines 264–293), following the same `MouseBinding` pattern used in `GalleryPage.xaml`:

```xml
<Border Margin="8,8,8,0" Background="#252525" CornerRadius="4" Padding="8"
        Cursor="Hand">
    <Border.InputBindings>
        <MouseBinding MouseAction="LeftClick"
                      Command="{Binding DataContext.OpenDetectionCommand,
                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                      CommandParameter="{Binding}"/>
    </Border.InputBindings>
    <StackPanel>
        ...
    </StackPanel>
</Border>
```

## Critical Files

| File | Role |
|------|------|
| `ViewModels/LiveViewModel.cs` | Add `ICaptureStorageService` dependency + `OpenDetectionCommand` |
| `Views/Pages/LiveViewPage.xaml` | Add `MouseBinding` + `Cursor="Hand"` to detection card |
| `Views/Dialogs/CaptureDetailDialog.xaml.cs` | Already exists — no changes needed |
| `Services/Interfaces/ICaptureStorageService.cs` | `GetCapturesByDateAsync` used for lookup — no changes needed |

## Verification

1. Run the app and connect to a camera (or load a video file)
2. Wait for at least one detection to appear in the Recent Detections panel
3. Click a detection card — `CaptureDetailDialog` should open showing the full capture detail
4. Verify the cursor changes to a hand pointer on hover over detection cards
5. Click a detection that was skipped (within cooldown) — nothing should happen (no error)
