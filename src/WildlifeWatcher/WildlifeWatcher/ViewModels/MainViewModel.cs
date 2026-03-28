using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;

namespace WildlifeWatcher.ViewModels;

public enum AppPage { LiveView, Gallery, Settings }

public partial class MainViewModel : ViewModelBase
{
    private readonly IRecognitionLoopService _recognitionLoop;
    private readonly IUpdateService          _updateService;
    private readonly ILogger<MainViewModel>  _logger;
    private readonly DispatcherTimer         _statusClearTimer;
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty] private AppPage _currentPage = AppPage.LiveView;
    [ObservableProperty] private double  _uiScale     = 1.0;
    [ObservableProperty] private string  _statusText  = "Ready";
    [ObservableProperty] private bool    _isConnected;
    [ObservableProperty] private bool    _isUpdateAvailable;
    [ObservableProperty] private string  _updateBannerText = string.Empty;
    [ObservableProperty] private bool    _isUpdating;
    [ObservableProperty] private int     _updateProgress;

    public string WindowTitle =>
        $"WildlifeWatcher v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    public MainViewModel(
        IRecognitionLoopService recognitionLoop,
        ICaptureStorageService  captureStorage,
        IUpdateService          updateService,
        ISettingsService        settings,
        ILogger<MainViewModel>  logger)
    {
        _recognitionLoop = recognitionLoop;
        _updateService   = updateService;
        _logger          = logger;

        UiScale = settings.CurrentSettings.UiScale;
        settings.SettingsChanged += (_, s) =>
            Application.Current.Dispatcher.Invoke(() => UiScale = s.UiScale);

        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusClearTimer.Tick += (_, _) => { StatusText = "Ready"; _statusClearTimer.Stop(); };

        captureStorage.CaptureSaved += (_, record) =>
            Application.Current.Dispatcher.Invoke(() => {
                StatusText = $"Saved: {record.Species.CommonName} ({record.ConfidenceScore:P0}) — {record.CapturedAt:HH:mm:ss}";
                _statusClearTimer.Stop();
                _statusClearTimer.Start();
            });

        // Background update check — fires after 3s then repeats every 5 minutes until update found
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                while (true)
                {
                    await Application.Current.Dispatcher.InvokeAsync(
                        () => CheckForUpdateCommand.ExecuteAsync(null)).Task.Unwrap();
                    if (IsUpdateAvailable) break;
                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background update check failed");
            }
        });
    }

    public event Action? OpenDetectionSettingsRequested;

    [RelayCommand]
    private void NavigateTo(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private void OpenDetectionSettings() => OpenDetectionSettingsRequested?.Invoke();

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        var info = await _updateService.CheckForUpdateAsync();
        if (info?.IsUpdateAvailable == true)
        {
            _pendingUpdate    = info;
            UpdateBannerText  = $"v{info.LatestVersion.ToString(3)} available — click Update Now";
            IsUpdateAvailable = true;
        }
    }

    [RelayCommand]
    private async Task ApplyUpdate()
    {
        if (_pendingUpdate == null) return;
        IsUpdating = true;
        var progress = new Progress<int>(p => UpdateProgress = p);
        await _updateService.ApplyUpdateAsync(_pendingUpdate, progress);
    }
}
