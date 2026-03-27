using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;
using WildlifeWatcher.Views.Dialogs;

namespace WildlifeWatcher.ViewModels;

public enum GalleryView     { SpeciesList, SpeciesDetail, CalendarView }
public enum GallerySortMode { NameAZ, LatinNameAZ, LatestCapture, TotalCaptures }

public partial class GalleryViewModel : ViewModelBase
{
    private const int CapturePageSize = 60;

    private readonly ICaptureStorageService _captureStorage;
    private readonly ISettingsService       _settings;
    private readonly IBirdPhotoService      _birdPhotoService;
    private readonly ILogger<GalleryViewModel> _logger;
    private List<SpeciesCardViewModel> _allSpecies = new();
    private SpeciesCardViewModel? _selectedSpecies;
    private int _photoFetchInProgress = 0; // 0 = idle, 1 = running (Interlocked flag)
    private int _currentSpeciesId;
    private int _capturePageOffset;

    [ObservableProperty] private GalleryView     _currentView        = GalleryView.SpeciesList;
    [ObservableProperty] private bool            _isRefreshingPhotos = false;
    [ObservableProperty] private string          _refreshStatusText  = string.Empty;
    [ObservableProperty] private bool            _isSelectionMode;
    [ObservableProperty] private bool            _hasMoreCaptures;
    [ObservableProperty] private int             _remainingCaptureCount;
    [ObservableProperty] private bool            _isLoadingMoreCaptures;

    public bool IsNotRefreshingPhotos => !IsRefreshingPhotos;
    public int  SelectedCount         => SelectedCaptures.Count(c => c.IsSelected);
    [ObservableProperty] private string          _searchText  = string.Empty;
    [ObservableProperty] private string          _selectedSpeciesName           = string.Empty;
    [ObservableProperty] private string          _selectedSpeciesScientificName = string.Empty;
    [ObservableProperty] private GallerySortMode _sortMode       = GallerySortMode.LatestCapture;
    [ObservableProperty] private bool            _sortDescending = true; // Latest/Count default to DESC

    // Day filter state
    [ObservableProperty] private bool _isFilteredByDay;
    [ObservableProperty] private int  _filterYear;
    [ObservableProperty] private int  _filterMonth;
    [ObservableProperty] private int  _filterDay;

    // All distinct capture dates for the current species — used to derive the three dropdown lists.
    private HashSet<DateTime> _availableDates = new();

    public ObservableCollection<int> AvailableFilterYears  { get; } = new();
    public ObservableCollection<int> AvailableFilterMonths { get; } = new();
    public ObservableCollection<int> AvailableFilterDays   { get; } = new();

    // Calendar state
    [ObservableProperty] private int _calendarYear  = DateTime.Today.Year;
    [ObservableProperty] private int _calendarMonth = DateTime.Today.Month;

    // Species list date filter — set when navigating from a calendar day click
    [ObservableProperty] private DateTime? _speciesListDateFilter;

    public bool   IsSpeciesListDateFiltered => SpeciesListDateFilter.HasValue;
    public string SpeciesListDateLabel      =>
        SpeciesListDateFilter.HasValue
            ? $"📅 {SpeciesListDateFilter.Value:d MMMM yyyy}"
            : string.Empty;

    public string CalendarMonthLabel =>
        new DateTime(CalendarYear, CalendarMonth, 1).ToString("MMMM yyyy");

    public bool IsShowingSpeciesList   => CurrentView == GalleryView.SpeciesList;
    public bool IsShowingSpeciesDetail => CurrentView == GalleryView.SpeciesDetail;
    public bool IsShowingCalendarView  => CurrentView == GalleryView.CalendarView;

    public ObservableCollection<SpeciesCardViewModel>      FilteredSpecies  { get; } = new();
    public RangeObservableCollection<CaptureCardViewModel> SelectedCaptures { get; } = new();
    public ObservableCollection<CalendarDayViewModel>      CalendarDays     { get; } = new();

    public GalleryViewModel(ICaptureStorageService captureStorage, ISettingsService settings, IBirdPhotoService birdPhotoService, ILogger<GalleryViewModel> logger)
    {
        _captureStorage   = captureStorage;
        _settings         = settings;
        _birdPhotoService = birdPhotoService;
        _logger           = logger;
        _captureStorage.CaptureSaved += OnCaptureSaved;
        _captureStorage.GalleryReset += async (_, _) =>
        {
            try
            {
                await Application.Current.Dispatcher
                    .InvokeAsync(() => LoadAsync()).Task.Unwrap();
                Application.Current.Dispatcher.Invoke(() => CurrentView = GalleryView.SpeciesList);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to reload gallery after reset"); }
        };
    }

    partial void OnSpeciesListDateFilterChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsSpeciesListDateFiltered));
        OnPropertyChanged(nameof(SpeciesListDateLabel));
    }

    partial void OnIsRefreshingPhotosChanged(bool value)
        => OnPropertyChanged(nameof(IsNotRefreshingPhotos));

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value) foreach (var c in SelectedCaptures) c.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    partial void OnCurrentViewChanged(GalleryView value)
    {
        OnPropertyChanged(nameof(IsShowingSpeciesList));
        OnPropertyChanged(nameof(IsShowingSpeciesDetail));
        OnPropertyChanged(nameof(IsShowingCalendarView));
    }

    // Sort button labels — show arrow only on the active sort
    public string SortNameLabel   => SortMode == GallerySortMode.NameAZ        ? $"Name {Arrow()}"   : "Name";
    public string SortLatinLabel  => SortMode == GallerySortMode.LatinNameAZ   ? $"Latin {Arrow()}"  : "Latin";
    public string SortLatestLabel => SortMode == GallerySortMode.LatestCapture ? $"Latest {Arrow()}" : "Latest";
    public string SortCountLabel  => SortMode == GallerySortMode.TotalCaptures ? $"Count {Arrow()}"  : "Count";
    private string Arrow() => SortDescending ? "▼" : "▲";

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortModeChanged(GallerySortMode value)
    {
        NotifySortLabels();
        ApplyFilter();
    }
    partial void OnSortDescendingChanged(bool value)
    {
        NotifySortLabels();
        ApplyFilter();
    }
    private void NotifySortLabels()
    {
        OnPropertyChanged(nameof(SortNameLabel));
        OnPropertyChanged(nameof(SortLatinLabel));
        OnPropertyChanged(nameof(SortLatestLabel));
        OnPropertyChanged(nameof(SortCountLabel));
    }

    partial void OnFilterYearChanged(int value)
    {
        RefreshAvailableMonths();
    }

    partial void OnFilterMonthChanged(int value)
    {
        RefreshAvailableDays();
    }

    private void RefreshAvailableMonths()
    {
        var months = _availableDates
            .Where(d => d.Year == FilterYear)
            .Select(d => d.Month)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        AvailableFilterMonths.Clear();
        foreach (var m in months) AvailableFilterMonths.Add(m);

        FilterMonth = months.Contains(FilterMonth) ? FilterMonth : (months.Count > 0 ? months[0] : 0);
    }

    private void RefreshAvailableDays()
    {
        var days = _availableDates
            .Where(d => d.Year == FilterYear && d.Month == FilterMonth)
            .Select(d => d.Day)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        AvailableFilterDays.Clear();
        foreach (var d in days) AvailableFilterDays.Add(d);

        FilterDay = days.Contains(FilterDay) ? FilterDay : (days.Count > 0 ? days[0] : 0);
    }

    private async void OnCaptureSaved(object? sender, CaptureRecord record)
    {
        if (!IsShowingSpeciesList) return;
        try { await Application.Current.Dispatcher.InvokeAsync(() => LoadAsync()).Task.Unwrap(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to reload gallery after capture saved"); }
    }

    public async Task LoadAsync()
    {
        SpeciesListDateFilter = null;
        var summaries = await _captureStorage.GetAllSpeciesSummariesAsync();
        _allSpecies = summaries.Select(s => new SpeciesCardViewModel(s)).ToList();
        ApplyFilter();

        // Background: fetch iNaturalist reference photos for species that don't have one yet
        var missing = summaries
            .Where(s => string.IsNullOrEmpty(s.ReferencePhotoPath) && !string.IsNullOrEmpty(s.ScientificName))
            .ToList();

        if (missing.Count > 0 && Interlocked.CompareExchange(ref _photoFetchInProgress, 1, 0) == 0)
            _ = Task.Run(() => FetchMissingPhotosAsync(missing));
    }

    private async Task FetchMissingPhotosAsync(List<SpeciesSummary> missing)
    {
        try
        {
            var retryQueue = new List<SpeciesSummary>();

            foreach (var s in missing)
            {
                var (path, fetchedFromNetwork, rateLimited) = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName);
                if (rateLimited) { retryQueue.Add(s); continue; }
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateSpeciesCard(s.SpeciesId, path));
                }
                if (fetchedFromNetwork) await Task.Delay(1100);
            }

            // Retry species that were skipped due to iNaturalist rate-limiting
            foreach (var s in retryQueue)
            {
                await Task.Delay(5000); // allow rate-limit window to recover before each retry
                var (path, _, _) = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName);
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                    await Application.Current.Dispatcher.InvokeAsync(() => UpdateSpeciesCard(s.SpeciesId, path));
                }
                await Task.Delay(1100);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch missing reference photos");
        }
        finally
        {
            Interlocked.Exchange(ref _photoFetchInProgress, 0);
        }
    }

    private void UpdateSpeciesCard(int speciesId, string newPhotoPath)
    {
        var idx = _allSpecies.FindIndex(c => c.Summary.SpeciesId == speciesId);
        if (idx < 0) return;
        var newCard = new SpeciesCardViewModel(_allSpecies[idx].Summary with { ReferencePhotoPath = newPhotoPath });
        var oldCard = _allSpecies[idx];
        _allSpecies[idx] = newCard;

        // Replace the single item in FilteredSpecies directly — avoids a full Clear+rebuild
        // and gives WPF a targeted Replace notification so the card redraws immediately.
        var filteredIdx = FilteredSpecies.IndexOf(oldCard);
        if (filteredIdx >= 0)
            FilteredSpecies[filteredIdx] = newCard;
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allSpecies
            : _allSpecies.Where(s =>
                s.Summary.CommonName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Summary.ScientificName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = SortMode switch
        {
            GallerySortMode.LatinNameAZ   => SortDescending
                                              ? (IEnumerable<SpeciesCardViewModel>)filtered.OrderByDescending(s => s.Summary.ScientificName)
                                              : filtered.OrderBy(s => s.Summary.ScientificName),
            GallerySortMode.LatestCapture => SortDescending
                                              ? filtered.OrderByDescending(s => s.LatestCaptureAt)
                                              : filtered.OrderBy(s => s.LatestCaptureAt),
            GallerySortMode.TotalCaptures => SortDescending
                                              ? filtered.OrderByDescending(s => s.CaptureCount)
                                              : filtered.OrderBy(s => s.CaptureCount),
            _                             => SortDescending
                                              ? filtered.OrderByDescending(s => s.Summary.CommonName)
                                              : filtered.OrderBy(s => s.Summary.CommonName),
        };

        FilteredSpecies.Clear();
        foreach (var s in sorted) FilteredSpecies.Add(s);
    }

    // ── Gallery commands ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshSpeciesPhotosAsync()
    {
        if (IsRefreshingPhotos) return;
        IsRefreshingPhotos = true;
        RefreshStatusText  = "Fetching photos…";
        try
        {
            var summaries  = await _captureStorage.GetAllSpeciesSummariesAsync();
            var withLatinName = summaries.Where(s => !string.IsNullOrEmpty(s.ScientificName)).ToList();
            int done = 0;

            var retryQueue = new List<SpeciesSummary>();

            foreach (var s in withLatinName)
            {
                RefreshStatusText = $"Fetching {s.CommonName}… ({done + 1}/{withLatinName.Count})";
                var (path, fetchedFromNetwork, rateLimited) = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName, forceRefresh: true);
                if (rateLimited) { retryQueue.Add(s); done++; continue; }
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                    UpdateSpeciesCard(s.SpeciesId, path);
                }
                done++;
                if (fetchedFromNetwork) await Task.Delay(1100);
            }

            // Retry rate-limited species
            foreach (var s in retryQueue)
            {
                RefreshStatusText = $"Retrying {s.CommonName}… (rate-limit recovery)";
                await Task.Delay(5000);
                var (path, _, _) = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName, forceRefresh: true);
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                    UpdateSpeciesCard(s.SpeciesId, path);
                }
                await Task.Delay(1100);
            }

            await Application.Current.Dispatcher.InvokeAsync(() => LoadAsync()).Task.Unwrap();
            RefreshStatusText = $"Updated {done} photo(s)";
            await Task.Delay(3000);
            RefreshStatusText = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during photo refresh");
            RefreshStatusText = "Refresh failed";
        }
        finally
        {
            IsRefreshingPhotos = false;
        }
    }

    [RelayCommand]
    private async Task BatchDeleteAsync()
    {
        var toDelete = SelectedCaptures.Where(c => c.IsSelected).ToList();
        if (toDelete.Count == 0) return;
        if (MessageBox.Show($"Delete {toDelete.Count} capture(s) permanently? This cannot be undone.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var card in toDelete)
            await _captureStorage.DeleteCaptureAsync(card.Record.Id);

        IsSelectionMode = false;
        await RefreshAfterBatchAsync();
    }

    [RelayCommand]
    private async Task BatchReassignAsync()
    {
        var toReassign = SelectedCaptures.Where(c => c.IsSelected).ToList();
        if (toReassign.Count == 0) return;

        var allSpecies = await _captureStorage.GetAllSpeciesAsync();
        var dialog     = new ReassignSpeciesDialog(allSpecies);
        if (dialog.ShowDialog() != true || !dialog.SelectedSpeciesId.HasValue) return;

        foreach (var card in toReassign)
            await _captureStorage.ReassignCaptureAsync(card.Record.Id, dialog.SelectedSpeciesId.Value);

        IsSelectionMode = false;
        await RefreshAfterBatchAsync();
    }

    private async Task RefreshAfterBatchAsync()
    {
        await LoadAsync();
        if (CurrentView == GalleryView.SpeciesDetail && _selectedSpecies != null)
        {
            var refreshed = _allSpecies.FirstOrDefault(s => s.Summary.SpeciesId == _selectedSpecies.Summary.SpeciesId);
            if (refreshed == null)
                CurrentView = GalleryView.SpeciesList;
            else
            {
                _selectedSpecies = refreshed;
                await LoadCapturesAsync(_selectedSpecies.Summary.SpeciesId);
            }
        }
    }

    [RelayCommand]
    private void Sort(string mode)
    {
        var newMode = mode switch
        {
            "LatinNameAZ"   => GallerySortMode.LatinNameAZ,
            "LatestCapture" => GallerySortMode.LatestCapture,
            "TotalCaptures" => GallerySortMode.TotalCaptures,
            _               => GallerySortMode.NameAZ,
        };

        if (newMode == SortMode)
        {
            SortDescending = !SortDescending; // toggle direction on re-click
        }
        else
        {
            // Name/Latin default ASC; Latest/Count default DESC
            SortDescending = newMode == GallerySortMode.LatestCapture || newMode == GallerySortMode.TotalCaptures;
            SortMode = newMode;
        }
    }

    [RelayCommand]
    private async Task OpenSpecies(SpeciesCardViewModel card)
    {
        _selectedSpecies               = card;
        SelectedSpeciesName            = card.Summary.CommonName;
        SelectedSpeciesScientificName  = card.Summary.ScientificName;
        await LoadCapturesAsync(card.Summary.SpeciesId);
        CurrentView = GalleryView.SpeciesDetail;

        // When drilling in from a calendar date-filtered list, auto-apply the day filter
        if (SpeciesListDateFilter.HasValue)
        {
            var d = SpeciesListDateFilter.Value;
            FilterYear  = d.Year;
            FilterMonth = d.Month;
            FilterDay   = d.Day;
            await FilterByDayAsync();
        }
    }

    [RelayCommand]
    private async Task ResetGalleryAsync()
    {
        if (MessageBox.Show("Delete all captures permanently? This cannot be undone.",
                "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await _captureStorage.ResetGalleryAsync(_settings.CurrentSettings.CapturesDirectory);
        await LoadAsync();
        CurrentView = GalleryView.SpeciesList;
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    [RelayCommand]
    private void Back()
    {
        IsSelectionMode  = false;
        IsFilteredByDay  = false;
        CurrentView      = GalleryView.SpeciesList;
        _selectedSpecies = null;
        // Keep SpeciesListDateFilter so the badge stays visible when returning to the filtered list
    }

    [RelayCommand]
    private async Task OpenCapture(CaptureCardViewModel card)
    {
        if (IsSelectionMode)
        {
            card.IsSelected = !card.IsSelected;
            OnPropertyChanged(nameof(SelectedCount));
            return;
        }

        var dialog = new CaptureDetailDialog(card.Record, _captureStorage);
        dialog.ShowDialog();

        // Only reload when a real mutation occurred (delete or reassign set DialogResult = true).
        // Plain close and save-notes leave DialogResult null — no gallery reload needed.
        if (dialog.DialogResult != true) return;

        await LoadAsync();

        if (CurrentView == GalleryView.SpeciesDetail && _selectedSpecies != null)
        {
            var refreshed = _allSpecies.FirstOrDefault(s => s.Summary.SpeciesId == _selectedSpecies.Summary.SpeciesId);
            if (refreshed == null)
                CurrentView = GalleryView.SpeciesList;
            else
            {
                _selectedSpecies = refreshed;
                await LoadCapturesAsync(_selectedSpecies.Summary.SpeciesId);
            }
        }
    }

    private async Task LoadCapturesAsync(int speciesId)
    {
        _currentSpeciesId  = speciesId;
        _capturePageOffset = 0;
        IsFilteredByDay    = false;

        // Load all distinct capture dates, then derive year/month/day dropdown lists.
        var dates = await _captureStorage.GetCaptureDatesForSpeciesAsync(speciesId);
        _availableDates = new HashSet<DateTime>(dates);

        var years = dates.Select(d => d.Year).Distinct().OrderByDescending(y => y).ToList();
        AvailableFilterYears.Clear();
        foreach (var y in years) AvailableFilterYears.Add(y);

        // Setting FilterYear triggers OnFilterYearChanged → RefreshAvailableMonths →
        // OnFilterMonthChanged → RefreshAvailableDays automatically.
        FilterYear = years.Count > 0 ? years[0] : DateTime.Today.Year;

        var captures = await _captureStorage.GetCapturesBySpeciesAsync(speciesId, 0, CapturePageSize);
        // Build VMs on background thread to keep File.Exists calls off the UI thread
        var cards = await Task.Run(() => captures.Select(c => new CaptureCardViewModel(c)).ToList());
        SelectedCaptures.ResetWith(cards); // single Reset notification → one layout pass

        _capturePageOffset = captures.Count;
        UpdateMoreCapturesState();
    }

    private void UpdateMoreCapturesState()
    {
        var total = _allSpecies.FirstOrDefault(s => s.Summary.SpeciesId == _currentSpeciesId)?.CaptureCount ?? 0;
        RemainingCaptureCount = total - _capturePageOffset;
        HasMoreCaptures       = RemainingCaptureCount > 0;
    }

    [RelayCommand]
    private async Task LoadMoreCapturesAsync()
    {
        if (!HasMoreCaptures) return;
        IsLoadingMoreCaptures = true;
        try
        {
            var captures = await _captureStorage.GetCapturesBySpeciesAsync(
                _currentSpeciesId, _capturePageOffset, CapturePageSize);
            // Build VMs on background thread to keep File.Exists calls off the UI thread
            var cards = await Task.Run(() => captures.Select(c => new CaptureCardViewModel(c)).ToList());
            SelectedCaptures.AddRange(cards); // single Add notification → one layout pass
            _capturePageOffset += captures.Count;
            UpdateMoreCapturesState();
        }
        finally
        {
            IsLoadingMoreCaptures = false;
        }
    }

    // ── Day filter commands ───────────────────────────────────────────────

    [RelayCommand]
    private async Task FilterByDayAsync()
    {
        if (FilterYear == 0 || FilterMonth == 0 || FilterDay == 0) return;
        DateTime date;
        try { date = new DateTime(FilterYear, FilterMonth, FilterDay); }
        catch (ArgumentOutOfRangeException) { return; }
        // Only filter if this date actually has captures (guard against stale selection)
        if (!_availableDates.Contains(date)) return;

        // Set flag BEFORE ResetWith so code-behind sees it when CollectionChanged fires
        IsFilteredByDay = true;
        var captures = await _captureStorage.GetCapturesBySpeciesAndDateAsync(_currentSpeciesId, date);
        var cards = await Task.Run(() => captures.Select(c => new CaptureCardViewModel(c)).ToList());
        SelectedCaptures.ResetWith(cards);
        HasMoreCaptures = false;
    }

    [RelayCommand]
    private async Task ShowAllCapturesAsync()
    {
        // Set flag BEFORE ResetWith so code-behind can restore pre-filter scroll offset
        IsFilteredByDay = false;
        await LoadCapturesAsync(_currentSpeciesId);
    }

    // ── Calendar commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void ShowSpeciesList()
    {
        CurrentView = GalleryView.SpeciesList;
    }

    [RelayCommand]
    private async Task ShowCalendar()
    {
        CurrentView = GalleryView.CalendarView;
        await LoadCalendarAsync();
    }

    [RelayCommand]
    private async Task NavigateCalendarForward()
    {
        var d = new DateTime(CalendarYear, CalendarMonth, 1).AddMonths(1);
        CalendarYear  = d.Year;
        CalendarMonth = d.Month;
        await LoadCalendarAsync();
    }

    [RelayCommand]
    private async Task NavigateCalendarBack()
    {
        var d = new DateTime(CalendarYear, CalendarMonth, 1).AddMonths(-1);
        CalendarYear  = d.Year;
        CalendarMonth = d.Month;
        await LoadCalendarAsync();
    }

    [RelayCommand]
    private async Task SelectDate(CalendarDayViewModel? day)
    {
        if (day is null || day.IsBlank || day.CaptureCount == 0) return;
        var date     = day.Date!.Value;
        var summaries = await _captureStorage.GetSpeciesSummariesForDateAsync(date);
        _allSpecies  = summaries.Select(s => new SpeciesCardViewModel(s)).ToList();
        SpeciesListDateFilter = date;
        ApplyFilter();
        CurrentView = GalleryView.SpeciesList;
    }

    [RelayCommand]
    private async Task ClearSpeciesDateFilter()
    {
        SpeciesListDateFilter = null;
        await LoadAsync();
    }

    private async Task LoadCalendarAsync()
    {
        CalendarDays.Clear();

        var summaries = await _captureStorage.GetCaptureDailySummaryForMonthAsync(CalendarYear, CalendarMonth);

        var firstDay    = new DateTime(CalendarYear, CalendarMonth, 1);
        var daysInMonth = DateTime.DaysInMonth(CalendarYear, CalendarMonth);

        // Monday-first offset
        var dow    = (int)firstDay.DayOfWeek;
        var offset = dow == 0 ? 6 : dow - 1;

        for (int i = 0; i < offset; i++)
            CalendarDays.Add(CalendarDayViewModel.Blank());

        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(CalendarYear, CalendarMonth, day);
            summaries.TryGetValue(date, out var summary);
            CalendarDays.Add(new CalendarDayViewModel(date, summary?.Count ?? 0, summary));
        }

        OnPropertyChanged(nameof(CalendarMonthLabel));
    }
}
