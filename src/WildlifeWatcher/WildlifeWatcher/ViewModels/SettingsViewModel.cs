using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

namespace WildlifeWatcher.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialService _credentialService;
    private readonly ICameraService _camera;
    private readonly IDbContextFactory<WildlifeDbContext> _dbFactory;
    private readonly ILogger<SettingsViewModel> _logger;

    // Track saved paths for migration detection
    private string _savedCapturesDirectory = string.Empty;
    private string _savedDatabasePath = string.Empty;

    // Camera
    [ObservableProperty] private string _rtspUrl = string.Empty;
    [ObservableProperty] private string _rtspUsername = string.Empty;
    [ObservableProperty] private string _rtspPassword = string.Empty;
    [ObservableProperty] private string _testConnectionStatus = string.Empty;
    [ObservableProperty] private Brush _testConnectionStatusColor = Brushes.Gray;

    // Capture
    [ObservableProperty] private int _cooldownSeconds = 30;
    [ObservableProperty] private int _frameIntervalSeconds = 30;
    [ObservableProperty] private string _capturesDirectory = "captures";
    [ObservableProperty] private double _minConfidenceThreshold = 0.7;
    [ObservableProperty] private double _motionSensitivity = 0.5;
    [ObservableProperty] private double _motionBackgroundAlpha = 0.05;
    [ObservableProperty] private int    _motionPixelThreshold = 15;

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

    [ObservableProperty] private string _saveStatus = string.Empty;

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

    public string SensitivityAdvice
    {
        get
        {
            double trigger = (1.0 - MotionSensitivity) * 0.08 * 100;
            return $"Triggers when ≥{trigger:F2}% of pixels differ from background. " +
                   (MotionSensitivity >= 0.97
                       ? "Very high — fires on almost any change; expect false positives."
                       : MotionSensitivity >= 0.7
                           ? "High — sensitive to small movement."
                           : MotionSensitivity <= 0.3
                               ? "Low — only large movements trigger."
                               : "Balanced.");
        }
    }

    public string PixelThresholdAdvice =>
        "Lower = catches subtle colour changes (birds on pavement); Higher = ignores noise/shadows. " +
        $"Current: {MotionPixelThreshold} — pixels must differ by >{MotionPixelThreshold}/255 intensity to count as changed.";

    partial void OnMotionBackgroundAlphaChanged(double value)  => OnPropertyChanged(nameof(AlphaAdvice));
    partial void OnFrameIntervalSecondsChanged(int value)      => OnPropertyChanged(nameof(AlphaAdvice));
    partial void OnMotionSensitivityChanged(double value)      => OnPropertyChanged(nameof(SensitivityAdvice));
    partial void OnMotionPixelThresholdChanged(int value)      => OnPropertyChanged(nameof(PixelThresholdAdvice));

    public SettingsViewModel(
        ISettingsService settingsService,
        ICredentialService credentialService,
        ICameraService camera,
        IDbContextFactory<WildlifeDbContext> dbFactory,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService   = settingsService;
        _credentialService = credentialService;
        _camera            = camera;
        _dbFactory         = dbFactory;
        _logger            = logger;

        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.CurrentSettings;
        RtspUrl                = s.RtspUrl;
        CooldownSeconds        = s.CooldownSeconds;
        FrameIntervalSeconds   = s.FrameExtractionIntervalSeconds;
        CapturesDirectory      = s.CapturesDirectory;
        MinConfidenceThreshold = s.MinConfidenceThreshold;
        MotionSensitivity      = s.MotionSensitivity;
        MotionBackgroundAlpha  = s.MotionBackgroundAlpha;
        MotionPixelThreshold   = s.MotionPixelThreshold;
        EnablePoiExtraction    = s.EnablePoiExtraction;
        SavePoiDebugImages     = s.SavePoiDebugImages;
        AiProvider             = s.AiProvider;
        ClaudeModel            = s.ClaudeModel;
        GeminiModel            = s.GeminiModel;
        DatabasePath           = s.DatabasePath;

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
                        var records = await db.CaptureRecords.ToListAsync();
                        foreach (var jpg in jpgs)
                        {
                            var dest = Path.Combine(newDir, Path.GetFileName(jpg));
                            File.Move(jpg, dest, overwrite: true);
                            var rec = records.FirstOrDefault(r => r.ImageFilePath == jpg);
                            if (rec != null) rec.ImageFilePath = dest;
                        }
                        await db.SaveChangesAsync();
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
            FrameExtractionIntervalSeconds = FrameIntervalSeconds,
            CapturesDirectory              = CapturesDirectory,
            MinConfidenceThreshold         = MinConfidenceThreshold,
            MotionSensitivity              = MotionSensitivity,
            MotionBackgroundAlpha          = MotionBackgroundAlpha,
            MotionPixelThreshold           = MotionPixelThreshold,
            AiProvider                     = AiProvider,
            ClaudeModel                    = ClaudeModel,
            GeminiModel                    = GeminiModel,
            EnableLocalPreFilter           = true,
            EnablePoiExtraction            = EnablePoiExtraction,
            SavePoiDebugImages             = SavePoiDebugImages,
            DatabasePath                   = DatabasePath,
            MotionWhitelistZones           = new List<MotionZone>(MotionZones.Select(z => z.ToMotionZone())),
        });
        _credentialService.SaveCredentials(RtspUsername, RtspPassword, AnthropicApiKey, GeminiApiKey);
        SaveStatus = "Settings saved.";
        _logger.LogInformation("Settings saved by user");
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
        var path = CapturesDirectory;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlifeWatcher", path);
        Directory.CreateDirectory(path);
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
}
