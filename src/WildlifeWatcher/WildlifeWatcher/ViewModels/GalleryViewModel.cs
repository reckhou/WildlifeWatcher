using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;
using WildlifeWatcher.Views.Dialogs;

namespace WildlifeWatcher.ViewModels;

public enum GalleryView { SpeciesList, SpeciesDetail }

public partial class GalleryViewModel : ViewModelBase
{
    private readonly ICaptureStorageService _captureStorage;
    private readonly ISettingsService       _settings;
    private List<SpeciesCardViewModel> _allSpecies = new();
    private SpeciesCardViewModel? _selectedSpecies;

    [ObservableProperty] private GalleryView _currentView = GalleryView.SpeciesList;
    [ObservableProperty] private string      _searchText  = string.Empty;
    [ObservableProperty] private string      _selectedSpeciesName           = string.Empty;
    [ObservableProperty] private string      _selectedSpeciesScientificName = string.Empty;

    public bool IsShowingSpeciesList   => CurrentView == GalleryView.SpeciesList;
    public bool IsShowingSpeciesDetail => CurrentView == GalleryView.SpeciesDetail;

    public ObservableCollection<SpeciesCardViewModel>  FilteredSpecies  { get; } = new();
    public ObservableCollection<CaptureCardViewModel>  SelectedCaptures { get; } = new();

    public GalleryViewModel(ICaptureStorageService captureStorage, ISettingsService settings)
    {
        _captureStorage = captureStorage;
        _settings       = settings;
        _captureStorage.CaptureSaved += OnCaptureSaved;
    }

    partial void OnCurrentViewChanged(GalleryView value)
    {
        OnPropertyChanged(nameof(IsShowingSpeciesList));
        OnPropertyChanged(nameof(IsShowingSpeciesDetail));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

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
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allSpecies
            : _allSpecies.Where(s =>
                s.Species.CommonName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Species.ScientificName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredSpecies.Clear();
        foreach (var s in filtered) FilteredSpecies.Add(s);
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

        // Refresh everything after the dialog closes (capture may have been deleted)
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
}
