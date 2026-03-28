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
    private string  _savedCapturesDirectory = string.Empty;
    private string  _savedDatabasePath      = string.Empty;
    private double? _selectedLatitude;
    private double? _selectedLongitude;
    private bool    _debugForceUpdateAvailable;

    // Camera
    [ObservableProperty] private string _rtspUrl = string.Empty;
    [ObservableProperty] private string _rtspUsername = string.Empty;
    [ObservableProperty] private string _rtspPassword = string.Empty;
    [ObservableProperty] private string _testConnectionStatus = string.Empty;
    [ObservableProperty] private Brush _testConnectionStatusColor = Brushes.Gray;

    // Data & Storage
    [ObservableProperty] private string _capturesDirectory = "captures";
    [ObservableProperty] private string _databasePath = string.Empty;

    // Location
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
        RtspUrl           = s.RtspUrl;
        CapturesDirectory = s.CapturesDirectory;
        DatabasePath      = s.DatabasePath;

        _selectedLatitude          = s.Latitude;
        _selectedLongitude         = s.Longitude;
        LocationName               = s.LocationName;
        _debugForceUpdateAvailable = s.DebugForceUpdateAvailable;

        _savedCapturesDirectory = s.CapturesDirectory;
        _savedDatabasePath      = s.DatabasePath;

        var creds = _credentialService.LoadCredentials();
        if (creds != null)
        {
            RtspUsername = creds.RtspUsername;
            RtspPassword = creds.RtspPassword;
        }
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
        // Mutate only Camera/Location/Data fields; detection settings are owned by DetectionSettingsViewModel
        var s = _settingsService.CurrentSettings;
        s.RtspUrl                    = RtspUrl;
        s.CapturesDirectory          = CapturesDirectory;
        s.DatabasePath               = DatabasePath;
        s.Latitude                   = _selectedLatitude;
        s.Longitude                  = _selectedLongitude;
        s.LocationName               = LocationName;
        s.DebugForceUpdateAvailable  = _debugForceUpdateAvailable;
        _settingsService.Save(s);

        // Preserve API keys — they are owned by DetectionSettingsViewModel
        var existingCreds = _credentialService.LoadCredentials();
        _credentialService.SaveCredentials(
            RtspUsername, RtspPassword,
            existingCreds?.AnthropicApiKey ?? string.Empty,
            existingCreds?.GeminiApiKey    ?? string.Empty);

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

            var job = new ImportJob
            {
                ZipPath = dialog.FileName,
                PreserveRtspUrl = _settingsService.CurrentSettings.RtspUrl,
                MainProcessId = Environment.ProcessId
            };
            var jobPath = Path.Combine(Path.GetTempPath(), $"ww_import_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job));

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
