using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;
using WildlifeWatcher.Views.Dialogs;


namespace WildlifeWatcher.ViewModels;

public partial class LiveViewModel : ViewModelBase
{
    private readonly ICameraService          _camera;
    private readonly IRecognitionLoopService _recognitionLoop;
    private readonly IBackgroundModelService _backgroundModel;
    private readonly ISettingsService        _settings;
    private readonly ILogger<LiveViewModel>  _logger;
    private readonly ICaptureStorageService  _captureStorage;
    private readonly ISunriseSunsetService   _daylightWindow;
    private readonly CancellationTokenSource _cts = new();
    private bool _userInitiatedDisconnect;

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _statusText           = "Not connected";
    [ObservableProperty] private string _connectionButtonText = "Connect";
    [ObservableProperty] private bool   _isAnalyzing;
    [ObservableProperty] private bool   _isBurstActive;
    [ObservableProperty] private double _burstProgress;
    [ObservableProperty] private string _burstProgressText    = string.Empty;
    [ObservableProperty] private string _lastDetectionText    = "No detections yet.";
    [ObservableProperty] private int    _volume;
    [ObservableProperty] private double _trainingProgress;
    [ObservableProperty] private bool   _isTrainingComplete   = true;
    [ObservableProperty] private string _trainingStatusText   = string.Empty;
    [ObservableProperty] private string _trainingTimeLeftText = string.Empty;
    [ObservableProperty] private string _modelDataAgeText     = string.Empty;
    [ObservableProperty] private string _daylightStatusText   = string.Empty;
    [ObservableProperty] private string _daylightFallbackText = string.Empty;
    [ObservableProperty] private BitmapSource? _hotCellOverlaySource;

    public MediaPlayer MediaPlayer => _camera.MediaPlayer;
    public ObservableCollection<DetectionEvent>   RecentDetections { get; } = new();
    public ObservableCollection<LogEntry>         LogEntries       { get; } = new();
    public ObservableCollection<PoiOverlayItem>   PoiOverlays      { get; } = new();

    public LiveViewModel(
        ICameraService           camera,
        IRecognitionLoopService  recognitionLoop,
        IBackgroundModelService  backgroundModel,
        ISettingsService         settings,
        ILogger<LiveViewModel>   logger,
        ICaptureStorageService   captureStorage,
        ISunriseSunsetService    daylightWindow)
    {
        _camera           = camera;
        _recognitionLoop  = recognitionLoop;
        _backgroundModel  = backgroundModel;
        _settings         = settings;
        _logger           = logger;
        _captureStorage   = captureStorage;
        _daylightWindow   = daylightWindow;

        _volume = _settings.CurrentSettings.Volume;

        _camera.ConnectionStateChanged           += OnConnectionStateChanged;
        _recognitionLoop.DetectionOccurred       += OnDetectionOccurred;
        _recognitionLoop.IsAnalyzingChanged      += OnIsAnalyzingChanged;
        _recognitionLoop.PoiRegionsDetected      += OnPoiRegionsDetected;
        _recognitionLoop.BurstProgressChanged    += OnBurstProgressChanged;
        _recognitionLoop.HotCellDebugComputed    += OnHotCellDebugComputed;
        _backgroundModel.TrainingProgressChanged += OnTrainingProgressChanged;
        _recognitionLoop.DaylightWindowChanged   += OnDaylightWindowChanged;
        InMemoryLogSink.EntryAdded               += OnLogEntryAdded;
        _settings.SettingsChanged                += OnSettingsChanged;

        _ = Task.Run(() => AutoConnectLoopAsync(_cts.Token));
    }

    partial void OnVolumeChanged(int value)
    {
        _camera.MediaPlayer.Volume = value;
        _settings.CurrentSettings.Volume = value;
        _settings.Save(_settings.CurrentSettings);
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (value) _userInitiatedDisconnect = false;
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        try
        {
            if (IsConnected)
            {
                _userInitiatedDisconnect = true;
                await _camera.DisconnectAsync();
            }
            else
            {
                _userInitiatedDisconnect = false;
                StatusText = "Connecting...";
                await _camera.ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling camera connection");
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadVideoFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "Open Video File for Playback",
            Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.ts;*.mpg;*.mpeg|All files|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        _userInitiatedDisconnect = false;
        StatusText = $"Loading: {Path.GetFileName(dialog.FileName)}…";
        _logger.LogInformation("Loading video file: {File}", dialog.FileName);
        await _camera.ConnectToFileAsync(dialog.FileName);
    }

    [RelayCommand]
    private void OpenCaptureFolder()
    {
        var path = _settings.CurrentSettings.CapturesDirectory;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeWatcher", path);

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    [RelayCommand]
    private void SkipTraining()
    {
        _backgroundModel.SkipTraining();
        OnTrainingProgressChanged(null, 1.0);
    }

    [RelayCommand]
    private void Retrain()
    {
        _backgroundModel.Reset();
        _backgroundModel.DeleteSavedState();
        _logger.LogInformation("Background model reset — retraining from scratch");

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsTrainingComplete   = false;
            TrainingProgress     = 0;
            TrainingStatusText   = "Training background model… 0%";
            TrainingTimeLeftText = ComputeTimeLeftText();
        });
    }

    [RelayCommand]
    private async Task OpenDetectionAsync(DetectionEvent e)
    {
        CaptureRecord? record = e.SavedRecord;

        // Fallback for events that pre-date the SavedRecord field (e.g. loaded from session)
        if (record is null)
        {
            var dayCaptures = await _captureStorage.GetCapturesByDateAsync(e.DetectedAt.Date);
            record = dayCaptures
                .Where(c => c.Species.CommonName == e.Result.CommonName)
                .MinBy(c => Math.Abs((c.CapturedAt - e.DetectedAt).Ticks));
        }

        if (record is null) return;

        var dialog = new CaptureDetailDialog(record, _captureStorage);
        dialog.ShowDialog();
    }

    // ── Auto-connect loop ─────────────────────────────────────────────────

    private async Task AutoConnectLoopAsync(CancellationToken ct)
    {
        await Task.Delay(1500, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            if (!_userInitiatedDisconnect && !IsConnected &&
                !string.IsNullOrWhiteSpace(_settings.CurrentSettings.RtspUrl))
            {
                _logger.LogInformation("Auto-connect: attempting connection...");
                _ = Application.Current.Dispatcher.InvokeAsync(() => StatusText = "Auto-connecting...");
                try
                {
                    await _camera.ConnectAsync();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Auto-connect failed");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        if (connected)
        {
            var loaded = _backgroundModel.LoadState();
            _logger.LogInformation(loaded
                ? "Background model restored from disk — skipping adaptation period"
                : "No saved background model found — cold-starting EMA");

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsTrainingComplete   = _backgroundModel.IsTrainingComplete;
                TrainingProgress     = _backgroundModel.TrainingProgress;
                TrainingStatusText   = IsTrainingComplete ? string.Empty : $"Training… {_backgroundModel.TrainingProgress:P0}";
                TrainingTimeLeftText = IsTrainingComplete ? string.Empty : ComputeTimeLeftText();
                ModelDataAgeText     = ComputeModelDataAgeText();
            });
        }
        else
        {
            _backgroundModel.SaveState();
            _logger.LogInformation("Background model saved to disk");
            _backgroundModel.Reset();

            Application.Current.Dispatcher.InvokeAsync(() => ModelDataAgeText = string.Empty);
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsConnected           = connected;
            StatusText            = connected ? "Connected" : "Disconnected";
            ConnectionButtonText  = connected ? "Disconnect" : "Connect";
            if (!connected) { PoiOverlays.Clear(); HotCellOverlaySource = null; }
        });
    }

    private void OnDetectionOccurred(object? sender, DetectionEvent e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RecentDetections.Insert(0, e);
            while (RecentDetections.Count > 5)
                RecentDetections.RemoveAt(5);
            LastDetectionText = $"Last: {e.Result.CommonName} ({e.Result.Confidence:P0})";
        });
    }

    private void OnIsAnalyzingChanged(object? sender, bool analyzing)
    {
        Application.Current.Dispatcher.InvokeAsync(() => IsAnalyzing = analyzing);
    }

    private void OnPoiRegionsDetected(object? sender, IReadOnlyList<PoiRegion> regions)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            PoiOverlays.Clear();
            foreach (var r in regions)
                PoiOverlays.Add(PoiOverlayItem.FromRegion(r));
        });
    }

    private void OnHotCellDebugComputed(object? sender, HotCellDebugData? data)
    {
        if (data is null)
        {
            Application.Current.Dispatcher.InvokeAsync(() => HotCellOverlaySource = null);
            return;
        }

        int cols = data.GridCols;
        int rows = data.GridRows;
        var pixels = new byte[rows * cols * 4]; // BGRA32

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            int i = (r * cols + c) * 4;
            switch (data.CellState[r, c])
            {
                case 3: // both foreground + temporal — pink (#FF4081 at ~53% opacity)
                case 4: // triggered feeder zone — pink
                    pixels[i] = 0x81; pixels[i + 1] = 0x40; pixels[i + 2] = 0xFF; pixels[i + 3] = 0x88;
                    break;
                case 1: // foreground only — amber (#FFC107 at ~53% opacity)
                    pixels[i] = 0x07; pixels[i + 1] = 0xC1; pixels[i + 2] = 0xFF; pixels[i + 3] = 0x88;
                    break;
                case 2: // temporal only — blue (#2196F3 at ~27% opacity)
                    pixels[i] = 0xF3; pixels[i + 1] = 0x96; pixels[i + 2] = 0x21; pixels[i + 3] = 0x44;
                    break;
                // case 0: transparent — already zeroed
            }
        }

        var bmp = BitmapSource.Create(cols, rows, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, cols * 4);
        bmp.Freeze();
        Application.Current.Dispatcher.InvokeAsync(() => HotCellOverlaySource = bmp);
    }

    private void OnBurstProgressChanged(object? sender, (int completed, int total) e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsBurstActive     = e.completed < e.total;
            BurstProgress     = e.total > 0 ? (double)e.completed / e.total : 0;
            BurstProgressText = IsBurstActive ? $"Burst {e.completed}/{e.total}" : string.Empty;
        });
    }

    private void OnTrainingProgressChanged(object? sender, double progress)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TrainingProgress     = progress;
            IsTrainingComplete   = _backgroundModel.IsTrainingComplete;
            TrainingStatusText   = IsTrainingComplete ? string.Empty : $"Training… {progress:P0}";
            TrainingTimeLeftText = IsTrainingComplete ? string.Empty : ComputeTimeLeftText();
            if (IsTrainingComplete)
                ModelDataAgeText = ComputeModelDataAgeText();
        });
    }

    private void OnDaylightWindowChanged(object? sender, bool allowed)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (allowed)
            {
                DaylightStatusText   = string.Empty;
                DaylightFallbackText = _daylightWindow.IsUsingFallback
                    ? "Daylight window active (no location set — using 06:00–20:00 fallback)"
                    : string.Empty;
            }
            else
            {
                var next = _daylightWindow.NextTransitionTime;
                DaylightStatusText   = $"Detection paused — outside daylight window (next: {next:HH:mm})";
                DaylightFallbackText = _daylightWindow.IsUsingFallback
                    ? "No location set — using 06:00–20:00 fallback"
                    : string.Empty;
            }
        });
    }

    private void OnSettingsChanged(object? sender, AppConfiguration settings)
    {
        if (!settings.ShowHotCellDebugOverlay)
            Application.Current.Dispatcher.InvokeAsync(() => HotCellOverlaySource = null);

        bool allowed = _daylightWindow.IsDetectionAllowed(settings);
        OnDaylightWindowChanged(this, allowed);
    }

    private string ComputeModelDataAgeText()
    {
        if (_backgroundModel.SavedAt is not { } savedAt)
            return string.Empty;
        var age = DateTime.UtcNow - savedAt;
        if (age.TotalSeconds < 90)  return "Model: just updated";
        if (age.TotalMinutes < 60)  return $"Model: {(int)age.TotalMinutes} min old";
        return $"Model: {(int)age.TotalHours}h {age.Minutes:D2}m old";
    }

    private string ComputeTimeLeftText()
    {
        int framesLeft = Math.Max(0, _backgroundModel.TrainingFramesNeeded - _backgroundModel.FrameCount);
        int secsLeft   = framesLeft * _settings.CurrentSettings.BackgroundUpdateIntervalSeconds;
        if (secsLeft <= 0) return string.Empty;
        return secsLeft >= 60 ? $"~{secsLeft / 60} min left" : $"~{secsLeft}s left";
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 200)
                LogEntries.RemoveAt(200);
        });
    }
}
