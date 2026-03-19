using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WildlifeWatcher.ViewModels.Base;

namespace WildlifeWatcher.ViewModels;

public enum AppPage { LiveView, Gallery, Settings }

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private AppPage _currentPage = AppPage.LiveView;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isConnected;

    [RelayCommand]
    private void NavigateTo(AppPage page) => CurrentPage = page;
}
