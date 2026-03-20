using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private List<SpeciesCardViewModel> _allSpecies = new();
    private SpeciesCardViewModel? _selectedSpecies;
    private int _photoFetchInProgress = 0; // 0 = idle, 1 = running (Interlocked flag)

    [ObservableProperty] private GalleryView     _currentView = GalleryView.SpeciesList;
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

    public GalleryViewModel(ICaptureStorageService captureStorage, ISettingsService settings, IBirdPhotoService birdPhotoService)
    {
        _captureStorage   = captureStorage;
        _settings         = settings;
        _birdPhotoService = birdPhotoService;
        _captureStorage.CaptureSaved += OnCaptureSaved;
    }

    partial void OnCurrentViewChanged(GalleryView value)
    {
        OnPropertyChanged(nameof(IsShowingSpeciesList));
        OnPropertyChanged(nameof(IsShowingSpeciesDetail));
        OnPropertyChanged(nameof(IsShowingCalendarView));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSortModeChanged(GallerySortMode value) => ApplyFilter();

    private void OnCaptureSaved(object? sender, CaptureRecord record)
    {
        if (IsShowingSpeciesList)
            Application.Current.Dispatcher.Invoke(() => _ = LoadAsync());
    }

    public async Task LoadAsync()
    {
        var species = await _captureStorage.GetAllSpeciesWithCapturesAsync();
        _allSpecies = species.Select(s => new SpeciesCardViewModel(s)).ToList();
        ApplyFilter();

        // Background: fetch iNaturalist reference photos for species that don't have one yet
        var missing = species
            .Where(s => string.IsNullOrEmpty(s.ReferencePhotoPath) && !string.IsNullOrEmpty(s.ScientificName))
            .ToList();

        if (missing.Count > 0 && Interlocked.CompareExchange(ref _photoFetchInProgress, 1, 0) == 0)
#pragma warning disable CS4014
            _ = Task.Run(() => FetchMissingPhotosAsync(missing));
#pragma warning restore CS4014
    }

    private async Task FetchMissingPhotosAsync(List<Species> missing)
    {
        try
        {
            var anyUpdated = false;
            foreach (var s in missing)
            {
                var path = await _birdPhotoService.FetchAndCachePhotoAsync(s.ScientificName);
                if (path != null)
                {
                    await _captureStorage.UpdateSpeciesReferencePhotoAsync(s.Id, path);
                    anyUpdated = true;
                }
                await Task.Delay(1100);
            }

            if (anyUpdated)
#pragma warning disable CS4014
                Application.Current.Dispatcher.Invoke(() => _ = LoadAsync());
#pragma warning restore CS4014
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
                s.Species.CommonName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Species.ScientificName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = SortMode switch
        {
            GallerySortMode.LatinNameAZ   => filtered.OrderBy(s => s.Species.ScientificName),
            GallerySortMode.LatestCapture => filtered.OrderByDescending(s => s.LatestCaptureAt),
            GallerySortMode.TotalCaptures => filtered.OrderByDescending(s => s.CaptureCount),
            _                             => filtered.OrderBy(s => s.Species.CommonName),
        };

        FilteredSpecies.Clear();
        foreach (var s in sorted) FilteredSpecies.Add(s);
    }

    // ── Gallery commands ──────────────────────────────────────────────────

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
        SelectedSpeciesName            = card.Species.CommonName;
        SelectedSpeciesScientificName  = card.Species.ScientificName;
        await LoadCapturesAsync(card.Species.Id);
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
    private void Back()
    {
        CurrentView = GalleryView.SpeciesList;
        _selectedSpecies = null;
    }

    [RelayCommand]
    private async Task OpenCapture(CaptureCardViewModel card)
    {
        var dialog = new CaptureDetailDialog(card.Record, _captureStorage);
        dialog.ShowDialog();

        await LoadAsync();

        if (CurrentView == GalleryView.SpeciesDetail && _selectedSpecies != null)
        {
            var stillExists = _allSpecies.Any(s => s.Species.Id == _selectedSpecies.Species.Id);
            if (!stillExists)
                CurrentView = GalleryView.SpeciesList;
            else
                await LoadCapturesAsync(_selectedSpecies.Species.Id);
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
        // Refresh calendar after dialog closes
        await LoadCalendarAsync();
        if (SelectedDate.HasValue)
        {
            var captures = await _captureStorage.GetCapturesByDateAsync(SelectedDate.Value);
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

        var counts = await _captureStorage.GetCaptureDateCountsForMonthAsync(CalendarYear, CalendarMonth);

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
            counts.TryGetValue(date, out var count);
            CalendarDays.Add(new CalendarDayViewModel(date, count));
        }

        OnPropertyChanged(nameof(CalendarMonthLabel));
        OnPropertyChanged(nameof(SelectedDateLabel));
    }
}
