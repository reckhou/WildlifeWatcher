# Phase 5 — Gallery

**Version:** `v0.4.0` → `v0.5.0`

## Context
Phase 4 persists captures to disk and DB. Phase 5 builds the Gallery UI so users can browse all detected wildlife grouped by species. Navigation: Gallery page (species grid) → Species Detail page (capture thumbnails) → Capture Detail dialog (full image + notes + delete). Sub-navigation within the gallery is handled internally by `GalleryViewModel` to keep `MainViewModel` simple.

**Decisions:**
- Gallery: species cards grid + search bar (filter by name as you type)
- Each species card: most recent photo, common name, scientific name, capture count, first/last seen
- Click species card → species detail page (within same content area)
- Species detail: thumbnail grid (newest first) + Back button
- Click thumbnail → modal dialog (full image, date, confidence, editable notes, delete button)

---

## New Files

| File | Purpose |
|---|---|
| `ViewModels/GalleryViewModel.cs` | Species grid + internal sub-navigation state |
| `ViewModels/SpeciesCardViewModel.cs` | Per-species card: preview image path, count, dates |
| `ViewModels/CaptureCardViewModel.cs` | Per-capture thumbnail: path, time label, confidence label |
| `Views/Pages/GalleryPage.xaml` + `.cs` | Main gallery page (hosts both views internally) |
| `Views/Dialogs/CaptureDetailDialog.xaml` + `.cs` | Modal: full image, notes editor, delete |
| `Converters/FilePathToImageConverter.cs` | File path `string` → `BitmapImage` for JPEG loading |

## Modified Files

| File | Change |
|---|---|
| `App.xaml.cs` | Register `GalleryViewModel`, `GalleryPage`; replace placeholder |
| `App.xaml` | Register `FilePathToImageConverter` |
| `Views/MainWindow.xaml.cs` | Inject `GalleryPage`; remove placeholder |

---

## Key Implementation Notes

### `FilePathToImageConverter`
```csharp
var img = new BitmapImage();
img.BeginInit();
img.UriSource = new Uri(path, UriKind.Absolute);
img.CacheOption = BitmapCacheOption.OnLoad;  // closes file handle immediately
img.DecodePixelWidth = 200;                  // decode at thumbnail size — saves memory
img.EndInit();
img.Freeze();
return img;
```
`DecodePixelWidth = 200` is critical for memory efficiency with many thumbnails.

### `SpeciesCardViewModel`
Snapshot class (no `ObservableObject`), built by `GalleryViewModel.LoadAsync()`:
```csharp
public Species Species { get; }
public int CaptureCount { get; }
public string FirstSeenLabel { get; }   // "First seen: 15 Mar 2026"
public string LastSeenLabel { get; }    // "Last seen: today" / "yesterday" / "19 Mar"
public string PreviewImagePath { get; } // ImageFilePath of most recent capture
```

### `GalleryViewModel` — sub-navigation
```csharp
public enum GalleryView { SpeciesList, SpeciesDetail }

[ObservableProperty] GalleryView _currentView = GalleryView.SpeciesList;
public bool IsShowingSpeciesList   => CurrentView == GalleryView.SpeciesList;
public bool IsShowingSpeciesDetail => CurrentView == GalleryView.SpeciesDetail;
```
Raise `PropertyChanged` for both bools from `OnCurrentViewChanged`.

**Species list with search:**
```csharp
private List<SpeciesCardViewModel> _allSpecies = new();
public ObservableCollection<SpeciesCardViewModel> FilteredSpecies { get; } = new();

[ObservableProperty] string _searchText = string.Empty;
partial void OnSearchTextChanged(string _) => ApplyFilter();
```
`ApplyFilter()`: filter `_allSpecies` by `CommonName` or `ScientificName` containing `SearchText` (case-insensitive).

**Commands:**
- `LoadAsync()`: `GetAllSpeciesWithCapturesAsync()` → build `SpeciesCardViewModel` list → `ApplyFilter()`
- `OpenSpeciesCommand(SpeciesCardViewModel)`: load captures → populate `SelectedCaptures` → `CurrentView = SpeciesDetail`
- `BackCommand`: `CurrentView = SpeciesList`
- `OpenCaptureCommand(CaptureCardViewModel)`: open `CaptureDetailDialog`; reload after close

**Live refresh:** Subscribe to `ICaptureStorageService.CaptureSaved` → if `IsShowingSpeciesList`, call `LoadAsync()`.

### `GalleryPage.xaml` — layout
Single `UserControl` with two `DockPanel`s toggled by `Visibility`:

**Species list view:**
```
DockPanel (Visibility=IsShowingSpeciesList):
  Search bar (DockPanel.Dock=Top): TextBox {SearchText}
  "No captures yet." (DataTrigger on FilteredSpecies.Count==0)
  ScrollViewer > ItemsControl (WrapPanel):
    Species card (Width=180):
      Image (Height=120, PreviewImagePath via FilePathToImage)
      CommonName, ScientificName, capture count, LastSeenLabel
      MouseBinding LeftClick → OpenSpeciesCommand
```

**Species detail view:**
```
DockPanel (Visibility=IsShowingSpeciesDetail):
  Header (DockPanel.Dock=Top): [← Back] CommonName, ScientificName
  ScrollViewer > ItemsControl (WrapPanel, virtualized):
    Capture card (Width=160):
      Image (Height=110)
      TimeLabel, ConfidenceLabel
      MouseBinding LeftClick → OpenCaptureCommand
```

Use `VirtualizingPanel.IsVirtualizing="True"` on the captures `ItemsControl` — prevents loading all thumbnails at once for species with many captures.

**Code-behind:**
```csharp
public GalleryPage(GalleryViewModel viewModel) {
    InitializeComponent();
    DataContext = viewModel;
    Loaded += async (_, _) => await viewModel.LoadAsync();
}
```

### `CaptureDetailDialog.xaml`
```
Window (600×500, ResizeMode=CanResize)
  Grid (Row 0=*: ScrollViewer > Image; Row 1=Auto: details panel)
  Details panel:
    Col 0: CommonName • date/time, Confidence, Notes TextBox (multiline)
    Col 1: [Save Notes] button, [Delete] button (red)
```

**Code-behind:**
```csharp
private async void SaveNotes_Click(...) {
    await _storage.UpdateCaptureNotesAsync(_record.Id, NotesBox.Text);
}
private async void Delete_Click(...) {
    if (MessageBox.Show("Delete this capture?", ..., YesNo) == Yes) {
        await _storage.DeleteCaptureAsync(_record.Id);
        DialogResult = true;
        Close();
    }
}
```
After dialog closes, `OpenCaptureCommand` calls `_ = LoadAsync()` to refresh counts/previews.

---

## Verification

1. `dotnet build` — 0 errors
2. Empty DB → Gallery shows "No captures yet."
3. After captures: species cards show correct thumbnails, counts, last-seen dates
4. Search "fox" → only fox card; clear → all cards return
5. Click species card → detail view; Back → species grid
6. Click thumbnail → dialog with full image, date, confidence, notes
7. Edit notes → Save → close → reopen → notes persisted
8. Delete → confirm → dialog closes → count decrements; last capture removed → species card disappears
9. New detection while gallery open → species card count updates
10. Species with 50+ captures → scroll without memory spike (virtualization working)
