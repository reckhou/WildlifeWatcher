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
    private readonly ICaptureStorageService _captureStorage;
    private readonly ISettingsService       _settings;
    private readonly IBirdPhotoService      _birdPhotoService;
    private readonly ILogger<GalleryViewModel> _logger;
    private List<SpeciesCardViewModel> _allSpecies = new();
    private SpeciesCardViewModel? _selectedSpecies;
    private int _photoFetchInProgress = 0; // 0 = idle, 1 = running (Interlocked flag)

    [ObservableProperty] private GalleryView     _currentView        = GalleryView.SpeciesList;
    [ObservableProperty] private bool            _isRefreshingPhotos = false;
    [ObservableProperty] private string          _refreshStatusText  = string.Empty;
    [ObservableProperty] private bool            _isSelectionMode;

    public bool IsNotRefreshingPhotos => !IsRefreshingPhotos;
    public int  SelectedCount         => SelectedCaptures.Count(c => c.IsSelected);
    [ObservableProperty] private string          _searchText  = string.Empty;
    [ObservableProperty] private string          _selectedSpeciesName           = string.Empty;
    [ObservableProperty] private string          _selectedSpeciesScientificName = string.Empty;
    [ObservableProperty] private GallerySortMode _sortMode    = GallerySortMode.LatestCapture;

    // Calendar state
    [ObservableProperty] private int       _calendarYear  = DateTime.Today.Year;
    [ObservableProperty] private int       _calendarMonth = DateTime.Today.Month;
    [ObservableProperty] private DateTime? _selectedDate;

    public string CalendarMonthLabel =>
        new DateTime(CalendarYear, CalendarMonth, 1).ToString("MMMM yyyy");

    public string SelectedDateLabel =>
        SelectedDate.HasValue
            ? $"{SelectedDate.Value:d MMMM yyyy} — {SelectedDayCaptures.Count} capture(s)"
            : string.Empty;

    public bool IsShowingSpeciesList   => CurrentView == GalleryView.SpeciesList;
    public bool IsShowingSpeciesDetail => CurrentView == GalleryView.SpeciesDetail;
    public bool IsShowingCalendarView  => CurrentView == GalleryView.CalendarView;

    public ObservableCollection<SpeciesCardViewModel>  FilteredSpecies     { get; } = new();
    public ObservableCollection<CaptureCardViewModel>  SelectedCaptures    { get; } = new();
    public ObservableCollection<CalendarDayViewModel>  CalendarDays        { get; } = new();
    public ObservableCollection<CaptureCardViewModel>  SelectedDayCaptures { get; } = new();

    public GalleryViewModel(ICaptureStorageService captureStorage, ISettingsService settings, IBirdPhotoService birdPhotoService, ILogger<GalleryViewModel> logger)
    {
        _captureStorage   = captureStorage;
        _settings         = settings;
        _birdPhotoService = birdPhotoService;
        _logger           = logger;
        _captureStorage.CaptureSaved += OnCaptureSaved;
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

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortModeChanged(GallerySortMode value) => ApplyFilter();

    private async void OnCaptureSaved(object? sender, CaptureRecord record)
    {
        if (!IsShowingSpeciesList) return;
        try { await Application.Current.Dispatcher.InvokeAsync(() => LoadAsync()).Task.Unwrap(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to reload gallery after capture saved"); }
    }

    public async Task LoadAsync()
    {
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
            var anyUpdated = false;
            foreach (var s in missing)
            {
                var path = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName);
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                    anyUpdated = true;
                }
                await Task.Delay(1100);
            }

            if (anyUpdated)
                await Application.Current.Dispatcher.InvokeAsync(() => LoadAsync()).Task.Unwrap();
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
            GallerySortMode.LatinNameAZ   => filtered.OrderBy(s => s.Summary.ScientificName),
            GallerySortMode.LatestCapture => filtered.OrderByDescending(s => s.LatestCaptureAt),
            GallerySortMode.TotalCaptures => filtered.OrderByDescending(s => s.CaptureCount),
            _                             => filtered.OrderBy(s => s.Summary.CommonName),
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

            foreach (var s in withLatinName)
            {
                RefreshStatusText = $"Fetching {s.CommonName}… ({done + 1}/{withLatinName.Count})";
                var path = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName, forceRefresh: true);
                if (path != null)
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.SpeciesId, path);
                done++;
                await Task.Delay(1100); // polite delay between requests
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
            var stillExists = _allSpecies.Any(s => s.Summary.SpeciesId == _selectedSpecies.Summary.SpeciesId);
            if (!stillExists)
                CurrentView = GalleryView.SpeciesList;
            else
                await LoadCapturesAsync(_selectedSpecies.Summary.SpeciesId);
        }
    }

    [RelayCommand]
    private void Sort(string mode)
    {
        SortMode = mode switch
        {
            "LatinNameAZ"   => GallerySortMode.LatinNameAZ,
            "LatestCapture" => GallerySortMode.LatestCapture,
            "TotalCaptures" => GallerySortMode.TotalCaptures,
            _               => GallerySortMode.NameAZ,
        };
    }

    [RelayCommand]
    private async Task OpenSpecies(SpeciesCardViewModel card)
    {
        _selectedSpecies               = card;
        SelectedSpeciesName            = card.Summary.CommonName;
        SelectedSpeciesScientificName  = card.Summary.ScientificName;
        await LoadCapturesAsync(card.Summary.SpeciesId);
        CurrentView = GalleryView.SpeciesDetail;
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
        CurrentView      = GalleryView.SpeciesList;
        _selectedSpecies = null;
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

        await LoadAsync();

        if (CurrentView == GalleryView.SpeciesDetail && _selectedSpecies != null)
        {
            var stillExists = _allSpecies.Any(s => s.Summary.SpeciesId == _selectedSpecies.Summary.SpeciesId);
            if (!stillExists)
                CurrentView = GalleryView.SpeciesList;
            else
                await LoadCapturesAsync(_selectedSpecies.Summary.SpeciesId);
        }
    }

    private async Task LoadCapturesAsync(int speciesId)
    {
        var captures = await _captureStorage.GetCapturesBySpeciesAsync(speciesId);
        SelectedCaptures.Clear();
        foreach (var c in captures)
            SelectedCaptures.Add(new CaptureCardViewModel(c));
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
        SelectedDate = day.Date;
        var captures = await _captureStorage.GetCapturesByDateAsync(day.Date!.Value);
        SelectedDayCaptures.Clear();
        foreach (var c in captures)
            SelectedDayCaptures.Add(new CaptureCardViewModel(c));
        OnPropertyChanged(nameof(SelectedDateLabel));
    }

    [RelayCommand]
    private async Task OpenDayCapture(CaptureCardViewModel card)
    {
        var dialog = new CaptureDetailDialog(card.Record, _captureStorage);
        dialog.ShowDialog();
        var savedDate = SelectedDate;
        await LoadCalendarAsync();
        if (savedDate.HasValue)
        {
            SelectedDate = savedDate;
            var captures = await _captureStorage.GetCapturesByDateAsync(savedDate.Value);
            SelectedDayCaptures.Clear();
            foreach (var c in captures)
                SelectedDayCaptures.Add(new CaptureCardViewModel(c));
            OnPropertyChanged(nameof(SelectedDateLabel));
        }
    }

    private async Task LoadCalendarAsync()
    {
        CalendarDays.Clear();
        SelectedDate = null;
        SelectedDayCaptures.Clear();

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
            CalendarDays.Add(new CalendarDayViewModel(date, summary?.Count ?? 0, summary?.WeatherCondition));
        }

        OnPropertyChanged(nameof(CalendarMonthLabel));
        OnPropertyChanged(nameof(SelectedDateLabel));
    }
}
