using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services;
using WildlifeWatcher.Services.Interfaces;
using WildlifeWatcher.ViewModels.Base;

namespace WildlifeWatcher.ViewModels;

public partial class DetectionSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService        _settings;
    private readonly ICredentialService      _credentials;
    private readonly ICameraService          _camera;
    private readonly IRecognitionLoopService _recognitionLoop;

    private const double ZoneCanvasW = 560;
    private const double ZoneCanvasH = 315;

    // ── Capture ────────────────────────────────────────────────────────────

    [ObservableProperty] private int    _cooldownSeconds        = 30;
    [ObservableProperty] private int    _speciesCooldownMinutes = 5;
    [ObservableProperty] private int    _frameIntervalSeconds   = 30;
    [ObservableProperty] private double _minConfidenceThreshold = 0.7;
    [ObservableProperty] private double _motionBackgroundAlpha  = 0.05;
    [ObservableProperty] private int    _motionPixelThreshold   = 25;
    [ObservableProperty] private int    _motionTemporalThreshold = 8;
    [ObservableProperty] private double _motionTemporalCellFraction = 0.10;

    // ── Point of Interest ──────────────────────────────────────────────────

    [ObservableProperty] private bool   _enablePoiExtraction = true;
    [ObservableProperty] private bool   _savePoiDebugImages  = true;
    [ObservableProperty] private double _poiSensitivity      = 0.5;
    [ObservableProperty] private int    _maxPoiRegions       = 5;

    // ── Grid Resolution ──────────────────────────────────────────────────

    [ObservableProperty] private int _poiCellSizePixels = 40;

    // ── Burst Mode ───────────────────────────────────────────────────────

    [ObservableProperty] private bool _enableBurstCapture = true;
    [ObservableProperty] private int  _burstFrameCount = 10;
    [ObservableProperty] private int  _burstIntervalMs = 1000;
    [ObservableProperty] private int  _backgroundUpdateIntervalSeconds = 2;

    // ── AI ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private AiProvider _aiProvider  = AiProvider.Claude;
    [ObservableProperty] private string     _claudeModel = "claude-haiku-4-5-20251001";
    [ObservableProperty] private string     _geminiModel = "gemini-2.0-flash";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptPreview))]
    private string _aiHabitatDescription = "a garden";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PromptPreview))]
    private string _aiTargetSpeciesHint = "Wildlife, particularly birds";

    // Not bound directly (PasswordBox can't bind) — held for pre-population in code-behind
    public string AnthropicApiKey { get; private set; } = string.Empty;
    public string GeminiApiKey    { get; private set; } = string.Empty;

    // ── Daylight ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _enableDaylightDetectionOnly;
    [ObservableProperty] private int  _sunriseOffsetMinutes = -30;
    [ObservableProperty] private int  _sunsetOffsetMinutes  = 30;

    // ── Motion Zones ───────────────────────────────────────────────────────

    [ObservableProperty] private byte[]? _zoneEditorBackground;
    [ObservableProperty] private bool    _isCapturingZoneBackground;
    public ObservableCollection<MotionZoneItem> MotionZones { get; } = new();

    // ── Display ────────────────────────────────────────────────────────────

    [ObservableProperty] private double _uiScale = 1.0;

    // ── Test POI ───────────────────────────────────────────────────────────

    [ObservableProperty] private string _testPoiStatus              = string.Empty;
    [ObservableProperty] private bool   _isTestPoiRunning;
    [ObservableProperty] private bool   _isContinuousTestRunning;
    [ObservableProperty] private int    _continuousTestIntervalSeconds = 5;

    private CancellationTokenSource? _continuousCts;

    // ── Provider list ──────────────────────────────────────────────────────

    public IEnumerable<AiProvider> AiProviders => Enum.GetValues<AiProvider>();

    // ── Computed advice ────────────────────────────────────────────────────

    public string AlphaAdvice
    {
        get
        {
            int frames  = (int)Math.Ceiling(Math.Log(0.05) / Math.Log(1 - MotionBackgroundAlpha));
            int seconds = frames * BackgroundUpdateIntervalSeconds;
            int minutes = seconds / 60;
            return $"At α={MotionBackgroundAlpha:F2} with {BackgroundUpdateIntervalSeconds}s interval: " +
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

    public string BurstAdvice =>
        EnableBurstCapture
            ? $"Burst: {BurstFrameCount} frames over {BurstFrameCount * BurstIntervalMs / 1000.0:F1}s. " +
              $"Auto-test floor: {BurstFrameCount * BurstIntervalMs / 1000 + 2}s."
            : "Burst disabled — single-frame POI only.";

    public string GridPresetAdvice
    {
        get
        {
            var preset = PoiCellSizePresets.All.FirstOrDefault(p => p.Size == PoiCellSizePixels);
            var name = preset.Name ?? $"Custom ({PoiCellSizePixels}px)";
            var desc = preset.Description ?? "";
            return $"{name} — {desc}";
        }
    }

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
        EnableDaylightDetectionOnly && string.IsNullOrWhiteSpace(_settings.CurrentSettings.LocationName);

    public string PromptPreview =>
        PromptBuilder.BuildPreview(AiHabitatDescription, _settings.CurrentSettings.LocationName, AiTargetSpeciesHint);

    // ── Changed hooks ──────────────────────────────────────────────────────

    partial void OnMotionBackgroundAlphaChanged(double value)  { OnPropertyChanged(nameof(AlphaAdvice)); AutoSave(); }
    partial void OnFrameIntervalSecondsChanged(int value)      { OnPropertyChanged(nameof(AlphaAdvice)); AutoSave(); }
    partial void OnMotionPixelThresholdChanged(int value)      { OnPropertyChanged(nameof(PixelThresholdAdvice)); AutoSave(); }
    partial void OnMotionTemporalThresholdChanged(int value)   { OnPropertyChanged(nameof(TemporalThresholdAdvice)); AutoSave(); }
    partial void OnPoiSensitivityChanged(double value)         { OnPropertyChanged(nameof(PoiSensitivityAdvice)); AutoSave(); }
    partial void OnMaxPoiRegionsChanged(int value)             => AutoSave();
    partial void OnPoiCellSizePixelsChanged(int value)         { OnPropertyChanged(nameof(GridPresetAdvice)); AutoSave(); }
    partial void OnEnableBurstCaptureChanged(bool value)       { OnPropertyChanged(nameof(BurstAdvice)); ClampContinuousTestInterval(); AutoSave(); }
    partial void OnBurstFrameCountChanged(int value)           { OnPropertyChanged(nameof(BurstAdvice)); ClampContinuousTestInterval(); AutoSave(); }
    partial void OnBurstIntervalMsChanged(int value)           { OnPropertyChanged(nameof(BurstAdvice)); ClampContinuousTestInterval(); AutoSave(); }
    partial void OnBackgroundUpdateIntervalSecondsChanged(int value) { OnPropertyChanged(nameof(AlphaAdvice)); AutoSave(); }
    partial void OnEnableDaylightDetectionOnlyChanged(bool value) { OnPropertyChanged(nameof(ShowDaylightLocationWarning)); AutoSave(); }

    partial void OnCooldownSecondsChanged(int value)              => AutoSave();
    partial void OnSpeciesCooldownMinutesChanged(int value)       => AutoSave();
    partial void OnMinConfidenceThresholdChanged(double value)    => AutoSave();
    partial void OnMotionTemporalCellFractionChanged(double value) => AutoSave();
    partial void OnEnablePoiExtractionChanged(bool value)         => AutoSave();
    partial void OnSavePoiDebugImagesChanged(bool value)          => AutoSave();
    partial void OnAiProviderChanged(AiProvider value)            => AutoSave();
    partial void OnClaudeModelChanged(string value)               => AutoSave();
    partial void OnGeminiModelChanged(string value)               => AutoSave();
    partial void OnAiHabitatDescriptionChanged(string value)       => AutoSave();
    partial void OnAiTargetSpeciesHintChanged(string value)        => AutoSave();
    partial void OnSunriseOffsetMinutesChanged(int value)         => AutoSave();
    partial void OnSunsetOffsetMinutesChanged(int value)          => AutoSave();

    // ── Constructor ────────────────────────────────────────────────────────

    public DetectionSettingsViewModel(
        ISettingsService        settings,
        ICredentialService      credentials,
        ICameraService          camera,
        IRecognitionLoopService recognitionLoop)
    {
        _settings        = settings;
        _credentials     = credentials;
        _camera          = camera;
        _recognitionLoop = recognitionLoop;

        LoadSettings();

        _settings.SettingsChanged += (_, s) =>
            Application.Current.Dispatcher.Invoke(() => UiScale = s.UiScale);
    }

    private bool _loadingSettings;

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            var s = _settings.CurrentSettings;
            CooldownSeconds              = s.CooldownSeconds;
            SpeciesCooldownMinutes       = s.SpeciesCooldownMinutes;
            FrameIntervalSeconds         = s.FrameExtractionIntervalSeconds;
            MinConfidenceThreshold       = s.MinConfidenceThreshold;
            MotionBackgroundAlpha        = s.MotionBackgroundAlpha;
            MotionPixelThreshold         = s.MotionPixelThreshold;
            MotionTemporalThreshold      = s.MotionTemporalThreshold;
            MotionTemporalCellFraction   = s.MotionTemporalCellFraction;
            EnablePoiExtraction          = s.EnablePoiExtraction;
            SavePoiDebugImages           = s.SavePoiDebugImages;
            PoiSensitivity               = s.PoiSensitivity;
            MaxPoiRegions                = s.MaxPoiRegions;
            PoiCellSizePixels            = s.PoiCellSizePixels;
            EnableBurstCapture           = s.EnableBurstCapture;
            BurstFrameCount              = s.BurstFrameCount;
            BurstIntervalMs              = s.BurstIntervalMs;
            BackgroundUpdateIntervalSeconds = s.BackgroundUpdateIntervalSeconds;
            AiProvider                   = s.AiProvider;
            ClaudeModel                  = s.ClaudeModel;
            GeminiModel                  = s.GeminiModel;
            AiHabitatDescription         = s.AiHabitatDescription;
            AiTargetSpeciesHint          = s.AiTargetSpeciesHint;
            EnableDaylightDetectionOnly  = s.EnableDaylightDetectionOnly;
            SunriseOffsetMinutes         = s.SunriseOffsetMinutes;
            SunsetOffsetMinutes          = s.SunsetOffsetMinutes;
            ContinuousTestIntervalSeconds = s.PoiTestIntervalSeconds;
            UiScale                      = s.UiScale;

            var creds = _credentials.LoadCredentials();
            if (creds != null)
            {
                AnthropicApiKey = creds.AnthropicApiKey;
                GeminiApiKey    = creds.GeminiApiKey;
            }

            RefreshZoneItems();
            OnPropertyChanged(nameof(PromptPreview));
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    // ── Auto-save ──────────────────────────────────────────────────────────

    private void AutoSave()
    {
        if (_loadingSettings) return;
        var s = _settings.CurrentSettings;
        s.CooldownSeconds                = CooldownSeconds;
        s.SpeciesCooldownMinutes         = SpeciesCooldownMinutes;
        s.FrameExtractionIntervalSeconds = FrameIntervalSeconds;
        s.MinConfidenceThreshold         = MinConfidenceThreshold;
        s.MotionBackgroundAlpha          = MotionBackgroundAlpha;
        s.MotionPixelThreshold           = MotionPixelThreshold;
        s.MotionTemporalThreshold        = MotionTemporalThreshold;
        s.MotionTemporalCellFraction     = MotionTemporalCellFraction;
        s.EnablePoiExtraction            = EnablePoiExtraction;
        s.SavePoiDebugImages             = SavePoiDebugImages;
        s.PoiSensitivity                 = PoiSensitivity;
        s.MaxPoiRegions                  = MaxPoiRegions;
        s.PoiCellSizePixels              = PoiCellSizePixels;
        s.EnableBurstCapture             = EnableBurstCapture;
        s.BurstFrameCount                = BurstFrameCount;
        s.BurstIntervalMs                = BurstIntervalMs;
        s.BackgroundUpdateIntervalSeconds = BackgroundUpdateIntervalSeconds;
        s.AiProvider                     = AiProvider;
        s.ClaudeModel                    = ClaudeModel;
        s.GeminiModel                    = GeminiModel;
        s.AiHabitatDescription            = string.IsNullOrWhiteSpace(AiHabitatDescription) ? "a garden" : AiHabitatDescription;
        s.AiTargetSpeciesHint             = AiTargetSpeciesHint;
        s.EnableDaylightDetectionOnly    = EnableDaylightDetectionOnly;
        s.SunriseOffsetMinutes           = SunriseOffsetMinutes;
        s.SunsetOffsetMinutes            = SunsetOffsetMinutes;
        s.PoiTestIntervalSeconds         = ContinuousTestIntervalSeconds;
        s.MotionWhitelistZones           = new List<MotionZone>(MotionZones.Select(z => z.ToMotionZone()));
        _settings.Save(s);
    }

    /// <summary>Called from code-behind when API key PasswordBoxes change.</summary>
    public void SaveApiCredentials(string anthropicKey, string geminiKey)
    {
        AnthropicApiKey = anthropicKey;
        GeminiApiKey    = geminiKey;
        var creds = _credentials.LoadCredentials();
        _credentials.SaveCredentials(
            creds?.RtspUsername ?? string.Empty,
            creds?.RtspPassword ?? string.Empty,
            anthropicKey,
            geminiKey);
    }

    // ── Motion Zone management ─────────────────────────────────────────────

    private void RefreshZoneItems()
    {
        MotionZones.Clear();
        var zones = _settings.CurrentSettings.MotionWhitelistZones;
        for (int i = 0; i < zones.Count; i++)
            MotionZones.Add(MotionZoneItem.From(zones[i], i + 1, ZoneCanvasW, ZoneCanvasH));
    }

    public void AddZone(MotionZone zone)
    {
        _settings.CurrentSettings.MotionWhitelistZones.Add(zone);
        RefreshZoneItems();
        AutoSave();
    }

    [RelayCommand]
    public void RemoveZone(MotionZoneItem item)
    {
        _settings.CurrentSettings.MotionWhitelistZones.RemoveAt(item.Index - 1);
        RefreshZoneItems();
        AutoSave();
    }

    [RelayCommand]
    public void ClearZones()
    {
        _settings.CurrentSettings.MotionWhitelistZones.Clear();
        MotionZones.Clear();
        AutoSave();
    }

    [RelayCommand]
    private async Task CaptureZoneBackgroundAsync()
    {
        IsCapturingZoneBackground = true;
        try
        {
            // Retry up to 3 times — TakeSnapshot can fail transiently right after
            // the camera connects (VLC decoder not yet producing frames).
            byte[]? frame = null;
            for (int i = 0; i < 3 && frame == null; i++)
            {
                frame = await _camera.ExtractFrameAsync();
                if (frame == null && i < 2)
                    await Task.Delay(500);
            }
            ZoneEditorBackground = frame;
        }
        finally { IsCapturingZoneBackground = false; }
    }

    public async Task AutoCaptureZoneBackgroundAsync()
    {
        if (_camera.IsConnected)
            await CaptureZoneBackgroundAsync();
    }

    partial void OnContinuousTestIntervalSecondsChanged(int value)
    {
        if (value < 1)  ContinuousTestIntervalSeconds = 1;
        else if (value > 30) ContinuousTestIntervalSeconds = 30;
        else AutoSave();
    }

    private void ClampContinuousTestInterval()
    {
        if (!EnableBurstCapture) return;
        int burstDurationSec = (int)Math.Ceiling(BurstFrameCount * BurstIntervalMs / 1000.0);
        int floor = burstDurationSec + 2;
        if (ContinuousTestIntervalSeconds < floor)
            ContinuousTestIntervalSeconds = floor;
    }

    // ── Test POI commands ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanTestPoi))]
    private async Task TestPoiAsync()
    {
        IsTestPoiRunning = true;
        TestPoiCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _recognitionLoop.TriggerTestPoiAsync();
            Application.Current.Dispatcher.Invoke(() => TestPoiStatus = result);
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => TestPoiStatus = $"Error: {ex.Message}");
        }
        finally
        {
            IsTestPoiRunning = false;
            TestPoiCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanTestPoi() => !IsTestPoiRunning && !IsContinuousTestRunning;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleContinuousTestAsync()
    {
        if (IsContinuousTestRunning)
        {
            _continuousCts?.Cancel();
            return;
        }

        IsContinuousTestRunning = true;
        TestPoiCommand.NotifyCanExecuteChanged();
        _continuousCts = new CancellationTokenSource();
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(ContinuousTestIntervalSeconds, 1, 30));
            using var timer = new System.Threading.PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_continuousCts.Token))
            {
                var result = await _recognitionLoop.TriggerTestPoiAsync();
                Application.Current.Dispatcher.Invoke(() => TestPoiStatus = $"[Auto] {result}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() => TestPoiStatus = $"[Auto] Error: {ex.Message}");
        }
        finally
        {
            IsContinuousTestRunning = false;
            _continuousCts?.Dispose();
            _continuousCts = null;
            TestPoiCommand.NotifyCanExecuteChanged();
        }
    }
}
