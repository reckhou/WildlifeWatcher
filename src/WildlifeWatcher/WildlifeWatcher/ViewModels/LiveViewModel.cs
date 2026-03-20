using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;


namespace WildlifeWatcher.ViewModels;

public partial class LiveViewModel : ViewModelBase
{
    private readonly ICameraService          _camera;
    private readonly IRecognitionLoopService _recognitionLoop;
    private readonly IBackgroundModelService _backgroundModel;
    private readonly ISettingsService        _settings;
    private readonly ILogger<LiveViewModel>  _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _userInitiatedDisconnect;

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private string _statusText           = "Not connected";
    [ObservableProperty] private string _connectionButtonText = "Connect";
    [ObservableProperty] private bool   _isAnalyzing;
    [ObservableProperty] private string _lastDetectionText    = "No detections yet.";
    [ObservableProperty] private int    _volume               = 100;
    [ObservableProperty] private double _trainingProgress;
    [ObservableProperty] private bool   _isTrainingComplete   = true;
    [ObservableProperty] private string _trainingStatusText   = string.Empty;
    [ObservableProperty] private string _trainingTimeLeftText = string.Empty;
    [ObservableProperty] private string _modelDataAgeText     = string.Empty;

    public MediaPlayer MediaPlayer => _camera.MediaPlayer;
    public ObservableCollection<DetectionEvent>   RecentDetections { get; } = new();
    public ObservableCollection<LogEntry>         LogEntries       { get; } = new();
    public ObservableCollection<PoiOverlayItem>   PoiOverlays      { get; } = new();

    public LiveViewModel(
        ICameraService           camera,
        IRecognitionLoopService  recognitionLoop,
        IBackgroundModelService  backgroundModel,
        ISettingsService         settings,
        ILogger<LiveViewModel>   logger)
    {
        _camera           = camera;
        _recognitionLoop  = recognitionLoop;
        _backgroundModel  = backgroundModel;
        _settings         = settings;
        _logger           = logger;

        _camera.ConnectionStateChanged           += OnConnectionStateChanged;
        _recognitionLoop.DetectionOccurred       += OnDetectionOccurred;
        _recognitionLoop.IsAnalyzingChanged      += OnIsAnalyzingChanged;
        _recognitionLoop.PoiRegionsDetected      += OnPoiRegionsDetected;
        _backgroundModel.TrainingProgressChanged += OnTrainingProgressChanged;
        InMemoryLogSink.EntryAdded               += OnLogEntryAdded;

        _ = Task.Run(() => AutoConnectLoopAsync(_cts.Token));
    }

    partial void OnVolumeChanged(int value) => _camera.MediaPlayer.Volume = value;

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

        Application.Current.Dispatcher.Invoke(() =>
        {
            IsTrainingComplete   = false;
            TrainingProgress     = 0;
            TrainingStatusText   = "Training background model… 0%";
            TrainingTimeLeftText = ComputeTimeLeftText();
        });
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
                Application.Current.Dispatcher.Invoke(() => StatusText = "Auto-connecting...");
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

            Application.Current.Dispatcher.Invoke(() =>
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

            Application.Current.Dispatcher.Invoke(() => ModelDataAgeText = string.Empty);
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            IsConnected           = connected;
            StatusText            = connected ? "Connected" : "Disconnected";
            ConnectionButtonText  = connected ? "Disconnect" : "Connect";
            if (!connected) PoiOverlays.Clear();
        });
    }

    private void OnDetectionOccurred(object? sender, DetectionEvent e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecentDetections.Insert(0, e);
            while (RecentDetections.Count > 5)
                RecentDetections.RemoveAt(5);
            LastDetectionText = $"Last: {e.Result.CommonName} ({e.Result.Confidence:P0})";
        });
    }

    private void OnIsAnalyzingChanged(object? sender, bool analyzing)
    {
        Application.Current.Dispatcher.Invoke(() => IsAnalyzing = analyzing);
    }

    private void OnPoiRegionsDetected(object? sender, IReadOnlyList<PoiRegion> regions)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PoiOverlays.Clear();
            foreach (var r in regions)
                PoiOverlays.Add(PoiOverlayItem.FromRegion(r));
        });
    }

    private void OnTrainingProgressChanged(object? sender, double progress)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            TrainingProgress     = progress;
            IsTrainingComplete   = _backgroundModel.IsTrainingComplete;
            TrainingStatusText   = IsTrainingComplete ? string.Empty : $"Training… {progress:P0}";
            TrainingTimeLeftText = IsTrainingComplete ? string.Empty : ComputeTimeLeftText();
            if (IsTrainingComplete)
                ModelDataAgeText = ComputeModelDataAgeText();
        });
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
        int secsLeft   = framesLeft * _settings.CurrentSettings.FrameExtractionIntervalSeconds;
        if (secsLeft <= 0) return string.Empty;
        return secsLeft >= 60 ? $"~{secsLeft / 60} min left" : $"~{secsLeft}s left";
    }

    private void OnLogEntryAdded(LogEntry entry)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Insert(0, entry);
            while (LogEntries.Count > 200)
                LogEntries.RemoveAt(200);
        });
    }
}
