using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Data;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;
using GeocodingResult = WildlifeWatcher.Services.Interfaces.GeocodingResult;

namespace WildlifeWatcher.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService        _settingsService;
    private readonly ICredentialService      _credentialService;
    private readonly ICameraService          _camera;
    private readonly IGeocodingService       _geocoding;
    private readonly IUpdateService          _updateService;
    private readonly IDataPortService        _dataPortService;
    private readonly ICaptureStorageService  _captureStorage;
    private readonly IDbContextFactory<WildlifeDbContext> _dbFactory;
    private readonly ILogger<SettingsViewModel> _logger;

    // Track saved paths for migration detection
    private string  _savedCapturesDirectory      = string.Empty;
    private string  _savedDatabasePath           = string.Empty;
    private double? _selectedLatitude;
    private double? _selectedLongitude;
    private bool    _debugForceUpdateAvailable;

    // Camera
    [ObservableProperty] private string _rtspUrl = string.Empty;
    [ObservableProperty] private string _rtspUsername = string.Empty;
    [ObservableProperty] private string _rtspPassword = string.Empty;
    [ObservableProperty] private string _testConnectionStatus = string.Empty;
    [ObservableProperty] private Brush _testConnectionStatusColor = Brushes.Gray;

    // Capture
    [ObservableProperty] private int _cooldownSeconds = 30;
    [ObservableProperty] private int _speciesCooldownMinutes = 5;
    [ObservableProperty] private int _frameIntervalSeconds = 30;
    [ObservableProperty] private string _capturesDirectory = "captures";
    [ObservableProperty] private double _minConfidenceThreshold = 0.7;
    [ObservableProperty] private double _motionBackgroundAlpha = 0.05;
    [ObservableProperty] private int    _motionPixelThreshold = 25;
    [ObservableProperty] private int    _motionTemporalThreshold = 8;
    [ObservableProperty] private double _motionTemporalCellFraction = 0.10;

    // Data & Storage
    [ObservableProperty] private string _databasePath = string.Empty;

    // AI
    [ObservableProperty] private AiProvider _aiProvider = AiProvider.Claude;
    [ObservableProperty] private string     _claudeModel = "claude-haiku-4-5-20251001";
    [ObservableProperty] private string     _anthropicApiKey = string.Empty;
    [ObservableProperty] private string     _geminiModel = "gemini-2.0-flash";
    [ObservableProperty] private string     _geminiApiKey = string.Empty;

    // POI
    [ObservableProperty] private bool _enablePoiExtraction = true;
    [ObservableProperty] private bool _savePoiDebugImages  = true;
    [ObservableProperty] private double _poiSensitivity    = 0.5;

    // Daylight detection
    [ObservableProperty] private bool _enableDaylightDetectionOnly;
    [ObservableProperty] private int  _sunriseOffsetMinutes = -30;
    [ObservableProperty] private int  _sunsetOffsetMinutes  = 30;

    // Location (Phase 6)
    [ObservableProperty] private string _locationQuery        = string.Empty;
    [ObservableProperty] private string _locationSearchStatus = string.Empty;
    [ObservableProperty] private string _locationName         = string.Empty;

    public ObservableCollection<GeocodingResult> LocationResults { get; } = new();
    public bool HasLocationResults => LocationResults.Count > 0;

    [ObservableProperty] private string _saveStatus = string.Empty;

    // Export / Import
    [ObservableProperty] private bool   _isExportImportBusy;
    [ObservableProperty] private string _exportImportStatus = string.Empty;
    [ObservableProperty] private int    _exportImportProgress;

    // Updates
    [ObservableProperty] private string _updateCheckStatus    = string.Empty;
    [ObservableProperty] private bool   _isUpdateCheckBusy;
    [ObservableProperty] private bool   _isUpdateReadyToInstall;
    [ObservableProperty] private bool   _isInstalling;
    [ObservableProperty] private int    _installProgress;
    private UpdateInfo? _pendingUpdate;

    // Motion Zones
    [ObservableProperty] private byte[]? _zoneEditorBackground;
    [ObservableProperty] private bool    _isCapturingZoneBackground;
    public ObservableCollection<MotionZoneItem> MotionZones { get; } = new();

    private const double ZoneCanvasW = 560;
    private const double ZoneCanvasH = 315;

    public IEnumerable<AiProvider> AiProviders => Enum.GetValues<AiProvider>();

    public string AlphaAdvice
    {
        get
        {
            int frames  = (int)Math.Ceiling(Math.Log(0.05) / Math.Log(1 - MotionBackgroundAlpha));
            int seconds = frames * FrameIntervalSeconds;
            int minutes = seconds / 60;
            return $"At α={MotionBackgroundAlpha:F2} with {FrameIntervalSeconds}s interval: " +
                   $"training completes in ~{minutes} min ({frames} frames). " +
                   (MotionBackgroundAlpha <= 0.05
                       ? "Slow adapt — better for persistent subjects, longer noise suppression."
                       : "Fast adapt — quicker day/night recovery, subjects absorbed sooner.");
        }
    }

    public string PoiSensitivityAdvice =>
        PoiSensitivity < 0.3
            ? "Conservative — detects only large, high-contrast subjects (pigeons, cats)"
            : PoiSensitivity <= 0.6
                ? "Balanced — good for medium-sized birds"
                : PoiSensitivity <= 0.85
                    ? "Sensitive — detects smaller birds (goldfinches, sparrows); may increase false positives"
                    : "Very sensitive — single-cell detection enabled; best for small/distant subjects but expect more noise";

    public string PixelThresholdAdvice =>
        MotionPixelThreshold < 15
            ? $"Warning: {MotionPixelThreshold} is below the noise floor — expect false triggers from camera noise and JPEG artifacts. Recommended: 20–30."
            : MotionPixelThreshold <= 20
                ? $"Current: {MotionPixelThreshold} — sensitive; may fire on compression artefacts in low light. Recommended: 20–30 for outdoor cameras."
                : MotionPixelThreshold <= 35
                    ? $"Current: {MotionPixelThreshold} — good balance. Ignores sensor noise/JPEG artefacts; detects real movement reliably. Recommended range: 20–30."
                    : $"Current: {MotionPixelThreshold} — high threshold; only strong contrast changes trigger. May miss small or camouflaged subjects.";

    public string TemporalThresholdAdvice =>
        MotionTemporalThreshold <= 5
            ? $"Warning: {MotionTemporalThreshold} is very low — may detect static shadows as motion. Recommended: 6–15."
            : MotionTemporalThreshold <= 12
                ? $"Current: {MotionTemporalThreshold} — good balance. Filters static shadows while detecting actual movement. Recommended range: 6–15."
                : $"Current: {MotionTemporalThreshold} — high threshold; only fast motion detected. May miss slow-moving subjects.";

    public bool ShowDaylightLocationWarning =>
        EnableDaylightDetectionOnly && string.IsNullOrWhiteSpace(LocationName) && _selectedLatitude is null;

    partial void OnMotionBackgroundAlphaChanged(double value)  => OnPropertyChanged(nameof(AlphaAdvice));
    partial void OnFrameIntervalSecondsChanged(int value)      => OnPropertyChanged(nameof(AlphaAdvice));
    partial void OnMotionPixelThresholdChanged(int value)      => OnPropertyChanged(nameof(PixelThresholdAdvice));
    partial void OnMotionTemporalThresholdChanged(int value)    => OnPropertyChanged(nameof(TemporalThresholdAdvice));
    partial void OnPoiSensitivityChanged(double value)        => OnPropertyChanged(nameof(PoiSensitivityAdvice));

    partial void OnEnableDaylightDetectionOnlyChanged(bool value) =>
        OnPropertyChanged(nameof(ShowDaylightLocationWarning));

    partial void OnLocationNameChanged(string value) =>
        OnPropertyChanged(nameof(ShowDaylightLocationWarning));

    public SettingsViewModel(
        ISettingsService         settingsService,
        ICredentialService       credentialService,
        ICameraService           camera,
        IGeocodingService        geocoding,
        IUpdateService           updateService,
        IDataPortService         dataPortService,
        ICaptureStorageService   captureStorage,
        IDbContextFactory<WildlifeDbContext> dbFactory,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService   = settingsService;
        _credentialService = credentialService;
        _camera            = camera;
        _geocoding         = geocoding;
        _updateService     = updateService;
        _dataPortService   = dataPortService;
        _captureStorage    = captureStorage;
        _dbFactory         = dbFactory;
        _logger            = logger;

        LocationResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLocationResults));

        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.CurrentSettings;
        RtspUrl                = s.RtspUrl;
        CooldownSeconds        = s.CooldownSeconds;
        SpeciesCooldownMinutes = s.SpeciesCooldownMinutes;
        FrameIntervalSeconds   = s.FrameExtractionIntervalSeconds;
        CapturesDirectory      = s.CapturesDirectory;
        MinConfidenceThreshold = s.MinConfidenceThreshold;
        MotionBackgroundAlpha  = s.MotionBackgroundAlpha;
        MotionPixelThreshold   = s.MotionPixelThreshold;
        MotionTemporalThreshold = s.MotionTemporalThreshold;
        MotionTemporalCellFraction = s.MotionTemporalCellFraction;
        EnablePoiExtraction    = s.EnablePoiExtraction;
        SavePoiDebugImages     = s.SavePoiDebugImages;
        PoiSensitivity         = s.PoiSensitivity;
        AiProvider             = s.AiProvider;
        ClaudeModel            = s.ClaudeModel;
        GeminiModel            = s.GeminiModel;
        DatabasePath           = s.DatabasePath;

        _selectedLatitude            = s.Latitude;
        _selectedLongitude           = s.Longitude;
        LocationName                 = s.LocationName;
        _debugForceUpdateAvailable   = s.DebugForceUpdateAvailable;
        EnableDaylightDetectionOnly = s.EnableDaylightDetectionOnly;
        SunriseOffsetMinutes        = s.SunriseOffsetMinutes;
        SunsetOffsetMinutes         = s.SunsetOffsetMinutes;

        _savedCapturesDirectory = s.CapturesDirectory;
        _savedDatabasePath      = s.DatabasePath;

        var creds = _credentialService.LoadCredentials();
        if (creds != null)
        {
            RtspUsername    = creds.RtspUsername;
            RtspPassword    = creds.RtspPassword;
            AnthropicApiKey = creds.AnthropicApiKey;
            GeminiApiKey    = creds.GeminiApiKey;
        }

        RefreshZoneItems();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // ── Captures path migration ──────────────────────────────────────
        if (CapturesDirectory != _savedCapturesDirectory)
        {
            var oldDir = ResolveDir(_savedCapturesDirectory);
            var newDir = ResolveDir(CapturesDirectory);
            if (oldDir != newDir && Directory.Exists(oldDir))
            {
                var jpgs = Directory.GetFiles(oldDir, "*.jpg");
                if (jpgs.Length > 0)
                {
                    var answer = MessageBox.Show(
                        $"Move {jpgs.Length} capture file(s) to the new location?\n{newDir}",
                        "Move Captures", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes)
                    {
                        Directory.CreateDirectory(newDir);
                        await using var db = await _dbFactory.CreateDbContextAsync();
                        foreach (var jpg in jpgs)
                        {
                            var fileName = Path.GetFileName(jpg);
                            var dest = Path.Combine(newDir, fileName);
                            await Task.Run(() => File.Move(jpg, dest, overwrite: true));
                            await db.CaptureRecords
                                .Where(r => r.ImageFilePath == jpg)
                                .ExecuteUpdateAsync(x => x.SetProperty(r => r.ImageFilePath, dest));
                        }
                        _logger.LogInformation("Moved {Count} capture file(s) to {Dir}", jpgs.Length, newDir);
                    }
                }
            }
            _savedCapturesDirectory = CapturesDirectory;
        }

        // ── Database path migration ──────────────────────────────────────
        if (DatabasePath != _savedDatabasePath)
        {
            var oldDb = ResolveDbPath(_savedDatabasePath);
            var newDb = ResolveDbPath(DatabasePath);
            if (oldDb != newDb && File.Exists(oldDb))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newDb)!);
                File.Copy(oldDb, newDb, overwrite: true);
                _logger.LogInformation("Database copied from {Old} to {New}", oldDb, newDb);
                MessageBox.Show(
                    $"Database moved to:\n{newDb}\n\nPlease restart the app to use the new location.",
                    "Restart Required", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            _savedDatabasePath = DatabasePath;
        }

        // ── Persist settings ─────────────────────────────────────────────
        _settingsService.Save(new AppConfiguration
        {
            RtspUrl                        = RtspUrl,
            CooldownSeconds                = CooldownSeconds,
            SpeciesCooldownMinutes         = SpeciesCooldownMinutes,
            FrameExtractionIntervalSeconds = FrameIntervalSeconds,
            CapturesDirectory              = CapturesDirectory,
            MinConfidenceThreshold         = MinConfidenceThreshold,
            MotionBackgroundAlpha          = MotionBackgroundAlpha,
            MotionPixelThreshold           = MotionPixelThreshold,
            MotionTemporalThreshold        = MotionTemporalThreshold,
            MotionTemporalCellFraction     = MotionTemporalCellFraction,
            AiProvider                     = AiProvider,
            ClaudeModel                    = ClaudeModel,
            GeminiModel                    = GeminiModel,
            EnablePoiExtraction            = EnablePoiExtraction,
            SavePoiDebugImages             = SavePoiDebugImages,
            PoiSensitivity                 = PoiSensitivity,
            DatabasePath                   = DatabasePath,
            MotionWhitelistZones           = new List<MotionZone>(MotionZones.Select(z => z.ToMotionZone())),
            Latitude                       = _selectedLatitude,
            Longitude                      = _selectedLongitude,
            LocationName                   = LocationName,
            DebugForceUpdateAvailable      = _debugForceUpdateAvailable,
            EnableDaylightDetectionOnly = EnableDaylightDetectionOnly,
            SunriseOffsetMinutes        = SunriseOffsetMinutes,
            SunsetOffsetMinutes         = SunsetOffsetMinutes,
        });
        _credentialService.SaveCredentials(RtspUsername, RtspPassword, AnthropicApiKey, GeminiApiKey);
        SaveStatus = "Settings saved.";
        _logger.LogInformation("Settings saved by user");
    }

    // ── Location search ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task SearchLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationQuery)) return;
        LocationSearchStatus = "Searching…";
        LocationResults.Clear();

        var results = await _geocoding.SearchAsync(LocationQuery);
        foreach (var r in results) LocationResults.Add(r);

        LocationSearchStatus = results.Count == 0 ? "No results found." : string.Empty;
    }

    [RelayCommand]
    private void SelectLocation(GeocodingResult result)
    {
        _selectedLatitude  = result.Latitude;
        _selectedLongitude = result.Longitude;
        LocationName       = result.DisplayName;
        LocationResults.Clear();
        LocationSearchStatus = string.Empty;
    }

    // ── Motion Zone management ────────────────────────────────────────────

    private void RefreshZoneItems()
    {
        MotionZones.Clear();
        var zones = _settingsService.CurrentSettings.MotionWhitelistZones;
        for (int i = 0; i < zones.Count; i++)
            MotionZones.Add(MotionZoneItem.From(zones[i], i + 1, ZoneCanvasW, ZoneCanvasH));
    }

    public void AddZone(MotionZone zone)
    {
        _settingsService.CurrentSettings.MotionWhitelistZones.Add(zone);
        RefreshZoneItems();
    }

    [RelayCommand]
    public void RemoveZone(MotionZoneItem item)
    {
        _settingsService.CurrentSettings.MotionWhitelistZones.RemoveAt(item.Index - 1);
        RefreshZoneItems();
    }

    [RelayCommand]
    public void ClearZones()
    {
        _settingsService.CurrentSettings.MotionWhitelistZones.Clear();
        MotionZones.Clear();
    }

    [RelayCommand]
    private async Task CaptureZoneBackgroundAsync()
    {
        IsCapturingZoneBackground = true;
        try   { ZoneEditorBackground = await _camera.ExtractFrameAsync(); }
        finally { IsCapturingZoneBackground = false; }
    }

    [RelayCommand]
    private void OpenCaptureFolder()
    {
        var path = Services.CaptureStorageService.ResolveCapturesDir(CapturesDirectory);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    [RelayCommand]
    private void BrowseCaptureFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description  = "Select captures folder",
            SelectedPath = ResolveDir(CapturesDirectory)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            CapturesDirectory = dialog.SelectedPath;
    }

    [RelayCommand]
    private void BrowseDatabase()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Select database file location",
            Filter           = "SQLite database|*.db",
            FileName         = "wildlife.db",
            InitialDirectory = Path.GetDirectoryName(ResolveDbPath(DatabasePath))
        };
        if (dialog.ShowDialog() == true)
            DatabasePath = dialog.FileName;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        TestConnectionStatus      = "Testing...";
        TestConnectionStatusColor = Brushes.Gray;
        try
        {
            var error = await _camera.TestConnectionAsync(RtspUrl, RtspUsername, RtspPassword);
            if (error == null)
            {
                TestConnectionStatus      = "Connection successful.";
                TestConnectionStatusColor = Brushes.Green;
            }
            else
            {
                TestConnectionStatus      = $"Failed: {error}";
                TestConnectionStatusColor = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test connection failed");
            TestConnectionStatus = $"Error: {ex.Message}";
        }
    }

    private static string ResolveDir(string configured)
    {
        if (Path.IsPathRooted(configured)) return configured;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WildlifeWatcher", configured);
    }

    private static string ResolveDbPath(string configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeWatcher", "wildlife.db")
            : configured;

    // ── Export / Import ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanExportImport))]
    private async Task ExportDataAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Wildlife Watcher data",
            Filter = "ZIP archive|*.zip",
            FileName = $"WildlifeWatcher_Export_{DateTime.Now:yyyyMMdd}.zip"
        };
        if (dialog.ShowDialog() != true) return;

        IsExportImportBusy = true;
        NotifyExportImportCanExecute();
        ExportImportStatus = string.Empty;
        ExportImportProgress = 0;
        try
        {
            var progress = new Progress<(int percent, string message)>(p =>
            {
                ExportImportProgress = p.percent;
                ExportImportStatus = p.message;
            });
            await _dataPortService.ExportAsync(dialog.FileName, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            ExportImportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExportImportBusy = false;
            NotifyExportImportCanExecute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportImport))]
    private async Task ImportDataAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Wildlife Watcher data",
            Filter = "ZIP archive|*.zip"
        };
        if (dialog.ShowDialog() != true) return;

        var answer = MessageBox.Show(
            "This will replace your current settings, database, and captures with the imported data.\n\n" +
            "API keys and camera credentials will NOT be affected.\n\n" +
            "The app will close and a progress window will handle the import, then restart.\n\nContinue?",
            "Import Data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            ExportImportStatus = "Preparing import…";

            // Write a job file for the import subprocess
            var job = new ImportJob
            {
                ZipPath = dialog.FileName,
                PreserveRtspUrl = _settingsService.CurrentSettings.RtspUrl,
                MainProcessId = Environment.ProcessId
            };
            var jobPath = Path.Combine(Path.GetTempPath(), $"ww_import_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job));

            // Launch a new instance in import mode, then exit so DB file locks are released
            var psi = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
            psi.ArgumentList.Add($"--import-job={jobPath}");
            Process.Start(psi);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch import");
            ExportImportStatus = $"Import failed: {ex.Message}";
        }
    }

    private bool CanExportImport() => !IsExportImportBusy;

    private void NotifyExportImportCanExecute()
    {
        ExportDataCommand.NotifyCanExecuteChanged();
        ImportDataCommand.NotifyCanExecuteChanged();
    }

    // ── Update commands ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsUpdateCheckBusy      = true;
        IsUpdateReadyToInstall = false;
        _pendingUpdate         = null;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();

        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        UpdateCheckStatus = "Checking for updates…";

        try
        {
            var info = await _updateService.CheckForUpdateAsync();
            if (info == null)
            {
                UpdateCheckStatus = "Could not reach update server.";
            }
            else if (info.IsUpdateAvailable)
            {
                _pendingUpdate         = info;
                IsUpdateReadyToInstall = true;
                UpdateCheckStatus      = $"v{info.LatestVersion.ToString(3)} is available (current: v{current.ToString(3)})";
            }
            else
            {
                UpdateCheckStatus = $"You are up to date (v{current.ToString(3)}).";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            UpdateCheckStatus = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsUpdateCheckBusy = false;
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckForUpdates() => !IsUpdateCheckBusy && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate == null) return;
        IsInstalling           = true;
        IsUpdateReadyToInstall = false;
        InstallUpdateCommand.NotifyCanExecuteChanged();
        UpdateCheckStatus = "Downloading update…";

        var progress = new Progress<int>(p =>
        {
            InstallProgress   = p;
            UpdateCheckStatus = $"Downloading… {p}%";
        });
        try
        {
            await _updateService.ApplyUpdateAsync(_pendingUpdate, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update install failed");
            UpdateCheckStatus      = $"Install failed: {ex.Message}";
            IsInstalling           = false;
            IsUpdateReadyToInstall = true;
            InstallUpdateCommand.NotifyCanExecuteChanged();
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstallUpdate() => IsUpdateReadyToInstall && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanForceUpdate))]
    private async Task ForceUpdateAsync()
    {
        IsUpdateCheckBusy = true;
        ForceUpdateCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        UpdateCheckStatus = "Fetching latest release…";

        try
        {
            var info = await _updateService.CheckForUpdateAsync(skipDebugOverride: true);
            if (info == null)
            {
                UpdateCheckStatus = "Could not reach update server.";
                return;
            }
            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                UpdateCheckStatus = "No release asset found — nothing to download.";
                return;
            }

            // Apply regardless of version comparison
            IsUpdateCheckBusy = false;
            IsInstalling      = true;
            InstallUpdateCommand.NotifyCanExecuteChanged();
            ForceUpdateCommand.NotifyCanExecuteChanged();

            var progress = new Progress<int>(p =>
            {
                InstallProgress   = p;
                UpdateCheckStatus = $"Downloading… {p}%";
            });
            await _updateService.ApplyUpdateAsync(info, progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force update failed");
            UpdateCheckStatus = $"Failed: {ex.Message}";
            IsInstalling      = false;
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            IsUpdateCheckBusy = false;
            ForceUpdateCommand.NotifyCanExecuteChanged();
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanForceUpdate() => !IsUpdateCheckBusy && !IsInstalling;

    // ── Danger Zone ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ResetGalleryAsync()
    {
        if (MessageBox.Show(
                "Delete all captures permanently? This cannot be undone.",
                "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        await _captureStorage.ResetGalleryAsync(_settingsService.CurrentSettings.CapturesDirectory);
    }
}
