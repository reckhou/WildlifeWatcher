# Species Detail Day Filter Implementation Plan

**Goal:** Add year/month/day dropdown date filtering to the Species Detail photo list, with Filter by Day and Show All buttons, and correct scroll position handling.

**Architecture:** Three new properties on `GalleryViewModel` (`FilterYear`, `FilterMonth`, `FilterDay`) plus a new `IsFilteredByDay` bool drive a `FilterByDayCommand` and `ShowAllCommand`. A new storage method `GetCapturesBySpeciesAndDateAsync` handles the date-scoped query. The code-behind watches `IsFilteredByDay` property changes to save/restore the pre-filter scroll offset into `_scrollPositions` before the existing `ScheduleScrollRestore` mechanism kicks in — no changes to the existing scroll-restore logic needed.

**Tech Stack:** C# + WPF + CommunityToolkit.Mvvm + EF Core

---

## Progress

- [x] Task 1: Add storage method `GetCapturesBySpeciesAndDateAsync`
- [x] Task 2: Add filter state and commands to `GalleryViewModel`
- [x] Task 3: Wire scroll position save/restore in `GalleryPage.xaml.cs`
- [x] Task 4: Add filter bar UI to `GalleryPage.xaml`

---

## Files

- Modify: `Services/Interfaces/ICaptureStorageService.cs` — add `GetCapturesBySpeciesAndDateAsync` to interface
- Modify: `Services/CaptureStorageService.cs` — implement the new method
- Modify: `ViewModels/GalleryViewModel.cs` — add filter properties, commands, available-year list
- Modify: `Views/Pages/GalleryPage.xaml.cs` — save pre-filter scroll; restore on Show All
- Modify: `Views/Pages/GalleryPage.xaml` — add filter bar below the header Border

---

### Task 1: Add storage method `GetCapturesBySpeciesAndDateAsync`

**Files:** `Services/Interfaces/ICaptureStorageService.cs`, `Services/CaptureStorageService.cs`

Add to interface:
```csharp
Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAndDateAsync(int speciesId, DateTime date);
Task<IReadOnlyList<int>> GetCaptureYearsForSpeciesAsync(int speciesId);
```

Implement in `CaptureStorageService.cs` following the same pattern as `GetCapturesByDateAsync`:
```csharp
public async Task<IReadOnlyList<CaptureRecord>> GetCapturesBySpeciesAndDateAsync(int speciesId, DateTime date)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    var start = date.Date;
    var end   = start.AddDays(1);
    return await db.CaptureRecords
        .Include(c => c.Species)
        .Where(c => c.SpeciesId == speciesId && c.CapturedAt >= start && c.CapturedAt < end)
        .OrderByDescending(c => c.CapturedAt)
        .ToListAsync();
}

public async Task<IReadOnlyList<int>> GetCaptureYearsForSpeciesAsync(int speciesId)
{
    await using var db = await _dbFactory.CreateDbContextAsync();
    return await db.CaptureRecords
        .Where(c => c.SpeciesId == speciesId)
        .Select(c => c.CapturedAt.Year)
        .Distinct()
        .OrderByDescending(y => y)
        .ToListAsync();
}
```

**Verify:** Project compiles — interface and implementation match.

---

### Task 2: Add filter state and commands to `GalleryViewModel`

**Files:** `ViewModels/GalleryViewModel.cs`

Add observable properties:
```csharp
[ObservableProperty] private bool _isFilteredByDay;
[ObservableProperty] private int  _filterYear;
[ObservableProperty] private int  _filterMonth = 1;
[ObservableProperty] private int  _filterDay   = 1;

public ObservableCollection<int> AvailableFilterYears { get; } = new();

// Static month/day lists for the ComboBoxes
public IReadOnlyList<int> AvailableMonths { get; } = Enumerable.Range(1, 12).ToList();
public IReadOnlyList<int> AvailableDays   { get; } = Enumerable.Range(1, 31).ToList();
```

Populate `AvailableFilterYears` inside `LoadCapturesAsync` (called when a species is opened):
```csharp
private async Task LoadCapturesAsync(int speciesId)
{
    _currentSpeciesId  = speciesId;
    _capturePageOffset = 0;
    IsFilteredByDay    = false;

    // Populate year dropdown
    var years = await _captureStorage.GetCaptureYearsForSpeciesAsync(speciesId);
    AvailableFilterYears.Clear();
    foreach (var y in years) AvailableFilterYears.Add(y);
    FilterYear  = years.Count > 0 ? years[0] : DateTime.Today.Year;
    FilterMonth = DateTime.Today.Month;
    FilterDay   = DateTime.Today.Day;

    // ... rest of existing load logic unchanged ...
}
```

Add commands:
```csharp
[RelayCommand]
private async Task FilterByDayAsync()
{
    if (FilterYear == 0 || FilterMonth == 0 || FilterDay == 0) return;
    var date = new DateTime(FilterYear, FilterMonth, FilterDay);
    var captures = await _captureStorage.GetCapturesBySpeciesAndDateAsync(_currentSpeciesId, date);
    var cards = await Task.Run(() => captures.Select(c => new CaptureCardViewModel(c)).ToList());
    IsFilteredByDay = true;  // set BEFORE ResetWith so code-behind sees it in time
    SelectedCaptures.ResetWith(cards);
    HasMoreCaptures = false;
}

[RelayCommand]
private async Task ShowAllCapturesAsync()
{
    IsFilteredByDay = false;  // set BEFORE ResetWith so code-behind restores pre-filter offset
    await LoadCapturesAsync(_currentSpeciesId);
}
```

**Important:** `IsFilteredByDay` must be set **before** `SelectedCaptures.ResetWith()` because the code-behind's `PropertyChanged` handler fires synchronously and needs to act before `ScheduleScrollRestore` is enqueued.

Also reset `IsFilteredByDay = false` in `Back()` command and `OpenSpecies()`.

**Verify:** Clicking "Filter by Day" in the running app shows only captures from the selected date. "Show All" restores all captures.

---

### Task 3: Wire scroll position save/restore in `GalleryPage.xaml.cs`

**Files:** `Views/Pages/GalleryPage.xaml.cs`

Add a second dictionary for pre-filter positions:
```csharp
private readonly Dictionary<string, double> _preFilterScrollPositions = new();
```

In the constructor, add a `PropertyChanged` handler alongside the existing `IsShowingSpeciesDetail` check:
```csharp
viewModel.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(GalleryViewModel.IsShowingSpeciesDetail) &&
        viewModel.IsShowingSpeciesDetail)
    {
        ScheduleScrollRestore();
    }

    if (e.PropertyName == nameof(GalleryViewModel.IsFilteredByDay))
    {
        var name = _vm.SelectedSpeciesName;
        if (string.IsNullOrEmpty(name)) return;

        if (_vm.IsFilteredByDay)
        {
            // Snapshot current scroll before filter loads
            _preFilterScrollPositions[name] = _scrollPositions.GetValueOrDefault(name, 0.0);
        }
        else
        {
            // Restore the pre-filter offset into _scrollPositions so ScheduleScrollRestore
            // picks it up naturally (no changes needed to ScheduleScrollRestore logic).
            if (_preFilterScrollPositions.TryGetValue(name, out var offset))
                _scrollPositions[name] = offset;
        }
    }
};
```

Modify `ScheduleScrollRestore` so that when a filter is active, it scrolls to top instead of restoring:
```csharp
private void ScheduleScrollRestore()
{
    _suppressScrollSave = true;
    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_vm.IsFilteredByDay)
            GetCapturesScrollViewer()?.ScrollToVerticalOffset(0);
        else
            RestoreSpeciesScroll();
        _suppressScrollSave = false;
    });
}
```

**Verify:**
- Open a species, scroll down, apply a filter → list resets to top.
- Click "Show All" → scroll position returns to where it was before filtering.

---

### Task 4: Add filter bar UI to `GalleryPage.xaml`

**Files:** `Views/Pages/GalleryPage.xaml`

Insert a new `<Border>` as a `DockPanel.Dock="Top"` element after the batch action bar (around line 312), before the loading indicator footer:

```xaml
<!-- Day filter bar -->
<Border DockPanel.Dock="Top" Background="#F5F5F5" Padding="16,8"
        BorderBrush="#DDDDDD" BorderThickness="0,0,0,1">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="Filter by day:" FontSize="12" Foreground="#555"
                   VerticalAlignment="Center" Margin="0,0,8,0"/>

        <!-- Year -->
        <ComboBox ItemsSource="{Binding AvailableFilterYears}"
                  SelectedItem="{Binding FilterYear, Mode=TwoWay}"
                  Width="70" FontSize="12" Margin="0,0,4,0"
                  VerticalContentAlignment="Center"/>

        <!-- Month -->
        <ComboBox ItemsSource="{Binding AvailableMonths}"
                  SelectedItem="{Binding FilterMonth, Mode=TwoWay}"
                  Width="46" FontSize="12" Margin="0,0,4,0"
                  VerticalContentAlignment="Center"/>

        <!-- Day -->
        <ComboBox ItemsSource="{Binding AvailableDays}"
                  SelectedItem="{Binding FilterDay, Mode=TwoWay}"
                  Width="46" FontSize="12" Margin="0,0,12,0"
                  VerticalContentAlignment="Center"/>

        <Button Content="Filter" Command="{Binding FilterByDayCommand}"
                Background="#2D5A27" Foreground="White" BorderThickness="0"
                Padding="10,4" FontSize="12" Cursor="Hand" Margin="0,0,8,0"/>

        <Button Content="Show All" Command="{Binding ShowAllCapturesCommand}"
                Background="#555555" Foreground="White" BorderThickness="0"
                Padding="10,4" FontSize="12" Cursor="Hand"
                Visibility="{Binding IsFilteredByDay,
                             Converter={StaticResource BoolToVisibleConverter}}"/>
    </StackPanel>
</Border>
```

**Verify:**
- Filter bar is always visible in the Species Detail view (not gated on selection mode).
- "Show All" button only appears when a filter is active.
- Year ComboBox is pre-populated with years from actual captures.
- Selecting year/month/day and clicking "Filter" shows only photos from that date; "Show All" restores all.
