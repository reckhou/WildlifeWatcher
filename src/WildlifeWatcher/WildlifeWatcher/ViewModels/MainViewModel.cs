using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;

namespace WildlifeWatcher.ViewModels;

public enum AppPage { LiveView, Gallery, Settings }

public partial class MainViewModel : ViewModelBase
{
    private readonly IRecognitionLoopService _recognitionLoop;
    private readonly DispatcherTimer _statusClearTimer;

    [ObservableProperty]
    private AppPage _currentPage = AppPage.LiveView;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isDebugMode;

    public MainViewModel(IRecognitionLoopService recognitionLoop, ICaptureStorageService captureStorage)
    {
        _recognitionLoop = recognitionLoop;

        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusClearTimer.Tick += (_, _) => { StatusText = "Ready"; _statusClearTimer.Stop(); };

        captureStorage.CaptureSaved += (_, record) =>
            Application.Current.Dispatcher.Invoke(() => {
                StatusText = $"Saved: {record.Species.CommonName} ({record.ConfidenceScore:P0}) — {record.CapturedAt:HH:mm:ss}";
                _statusClearTimer.Stop();
                _statusClearTimer.Start();
            });
    }

    partial void OnIsDebugModeChanged(bool value) =>
        _recognitionLoop.IsDebugMode = value;

    [RelayCommand]
    private void NavigateTo(AppPage page) => CurrentPage = page;
}
