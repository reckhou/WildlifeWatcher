using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class RecognitionLoopService : IHostedService, IRecognitionLoopService, IDisposable
{
    private readonly ICameraService              _camera;
    private readonly IAiRecognitionService       _ai;
    private readonly IPointOfInterestService     _poi;
    private readonly IBackgroundModelService     _background;
    private readonly ISettingsService            _settings;
    private readonly ICaptureStorageService      _captureStorage;
    private readonly ISunriseSunsetService       _daylightWindow;
    private readonly ILogger<RecognitionLoopService> _logger;

    private CancellationTokenSource? _cts;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private bool _wasInDaylightWindow = true;

    public bool IsRunning   { get; private set; }
    public bool IsAnalyzing { get; private set; }

    public event EventHandler<DetectionEvent>?               DetectionOccurred;
    public event EventHandler<bool>?                         IsAnalyzingChanged;
    public event EventHandler<IReadOnlyList<PoiRegion>>?     PoiRegionsDetected;
    public event EventHandler<bool>?                         DaylightWindowChanged;

    public RecognitionLoopService(
        ICameraService              camera,
        IAiRecognitionService       ai,
        IPointOfInterestService     poi,
        IBackgroundModelService     background,
        ISettingsService            settings,
        ISunriseSunsetService       daylightWindow,
        ICaptureStorageService      captureStorage,
        ILogger<RecognitionLoopService> logger)
    {
        _camera         = camera;
        _ai             = ai;
        _poi            = poi;
        _background     = background;
        _settings       = settings;
        _daylightWindow = daylightWindow;
        _captureStorage = captureStorage;
        _logger         = logger;

        _settings.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, AppConfiguration settings)
    {
        bool allowed = _daylightWindow.IsDetectionAllowed(settings);
        FireDaylightWindowChanged(allowed);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        _logger.LogInformation("Recognition loop started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        IsRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("Recognition loop stopped");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ── Main loop ─────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var interval = TimeSpan.FromSeconds(_settings.CurrentSettings.FrameExtractionIntervalSeconds);
                await Task.Delay(interval, ct);
                if (!ct.IsCancellationRequested)
                    await ProcessTickAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in recognition loop");
        }
    }

    private async Task ProcessTickAsync(CancellationToken ct)
    {
        if (!_camera.IsConnected) return;

        var currentFrame = await _camera.ExtractFrameAsync();
        if (currentFrame == null) return;

        var settings = _settings.CurrentSettings;

        // Clear POI overlay at the start of each tick
        PoiRegionsDetected?.Invoke(this, Array.Empty<PoiRegion>());

        // ── Update background model ──────────────────────────────────────
        _background.ProcessFrame(currentFrame);
        var fg = _background.Foreground;
        var temporalDelta = _background.TemporalDelta;
        if (fg == null) return; // first frame — no background established yet

        // ── Training gate: defer POI + AI until background model is ready ─
        if (!_background.IsTrainingComplete)
        {
            _logger.LogInformation(
                "Background training {Progress:P0} ({Frames}/{Needed}) — waiting for model to stabilise",
                _background.TrainingProgress, _background.FrameCount, _background.TrainingFramesNeeded);
            return;
        }

        // ── Trigger daily sunrise/sunset refresh (fire-and-forget) ───────────
        _ = _daylightWindow.RefreshIfNeededAsync(settings);

        // ── Daylight window gate ─────────────────────────────────────────────
        if (settings.EnableDaylightDetectionOnly && !_daylightWindow.IsDetectionAllowed(settings))
        {
            var next = _daylightWindow.NextTransitionTime;
            _logger.LogInformation(
                "Outside daylight window — detection paused until {NextTransition:HH:mm}", next);
            FireDaylightWindowChanged(false);
            return;
        }
        FireDaylightWindowChanged(true);

        var zones = settings.MotionWhitelistZones.Count > 0
            ? (IReadOnlyList<MotionZone>)settings.MotionWhitelistZones : null;

        // ── POI extraction ───────────────────────────────────────────────
        IReadOnlyList<PoiRegion> poiRegions = Array.Empty<PoiRegion>();
        if (settings.EnablePoiExtraction)
        {
            poiRegions = _poi.ExtractRegions(fg, currentFrame, zones, settings.MotionPixelThreshold, settings.PoiSensitivity,
                temporalDelta, settings.MotionTemporalThreshold, settings.MotionTemporalCellFraction);
            _logger.LogInformation("POI extraction: {Count} region(s) found", poiRegions.Count);
            PoiRegionsDetected?.Invoke(this, poiRegions);

            if (poiRegions.Count == 0)
            {
                _logger.LogInformation("POI extraction found 0 regions — skipping AI call");
                return;
            }
        }

        // ── Cooldown check ───────────────────────────────────────────────
        if (DateTime.UtcNow < _cooldownUntil)
        {
            var remaining = (_cooldownUntil - DateTime.UtcNow).TotalSeconds;
            _logger.LogInformation("Cooldown active — skipping AI ({Remaining:F0}s remaining)", remaining);
            return;
        }

        // ── AI recognition ───────────────────────────────────────────────
        var activeModel = settings.AiProvider == WildlifeWatcher.Models.AiProvider.Gemini
            ? settings.GeminiModel
            : settings.ClaudeModel;
        _logger.LogInformation("Sending frame to AI ({Provider}/{Model})…", settings.AiProvider, activeModel);
        var batchStartedAt = DateTime.Now;
        SetAnalyzing(true);
        try
        {
            var poiJpegs = poiRegions.Count > 0
                ? (IReadOnlyList<byte[]>)poiRegions.Select(r => r.CroppedJpeg).ToArray()
                : null;

            var results = await _ai.RecognizeAsync(currentFrame, poiJpegs, ct);

            if (results.Count == 0 || results.All(r => !r.Detected))
            {
                _logger.LogInformation("AI result: no wildlife detected");
            }
            else
            {
                bool cooldownSet = false;
                foreach (var result in results.Where(r => r.Detected))
                {
                    if (result.Confidence < settings.MinConfidenceThreshold)
                    {
                        _logger.LogInformation(
                            "AI result: {Species} detected but confidence {Confidence:P0} below threshold {Threshold:P0}",
                            result.CommonName, result.Confidence, settings.MinConfidenceThreshold);
                        continue;
                    }

                    if (!cooldownSet)
                    {
                        _cooldownUntil = DateTime.UtcNow.AddSeconds(settings.CooldownSeconds);
                        cooldownSet    = true;
                    }

                    try { await _captureStorage.SaveCaptureAsync(currentFrame, result, poiRegions, batchStartedAt); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to save capture"); }

                    var evt = new DetectionEvent
                    {
                        DetectedAt = DateTime.Now,
                        Result     = result,
                        FramePng   = currentFrame,
                        PoiRegions = poiRegions
                    };
                    DetectionOccurred?.Invoke(this, evt);
                    _logger.LogInformation(
                        "✓ Detection: {Species} ({ScientificName}) — confidence {Confidence:P0}",
                        result.CommonName, result.ScientificName, result.Confidence);
                }
            }
        }
        finally
        {
            SetAnalyzing(false);
        }
    }

    // ── Test POI ──────────────────────────────────────────────────────────

    public async Task<string> TriggerTestPoiAsync()
    {
        if (!_camera.IsConnected)
            return "Camera not connected.";

        var frame = await _camera.ExtractFrameAsync();
        if (frame == null)
            return "Failed to extract frame.";

        var settings = _settings.CurrentSettings;

        _background.ProcessFrame(frame);
        var fg           = _background.Foreground;
        var temporalDelta = _background.TemporalDelta;
        if (fg == null)
            return "Background model not ready yet (first frame).";

        var zones = settings.MotionWhitelistZones.Count > 0
            ? (IReadOnlyList<MotionZone>)settings.MotionWhitelistZones : null;

        IReadOnlyList<PoiRegion> regions = Array.Empty<PoiRegion>();
        if (settings.EnablePoiExtraction)
        {
            regions = _poi.ExtractRegions(fg, frame, zones, settings.MotionPixelThreshold,
                settings.PoiSensitivity, temporalDelta, settings.MotionTemporalThreshold,
                settings.MotionTemporalCellFraction);
            _logger.LogInformation("Test POI extraction: {Count} region(s) found", regions.Count);
            PoiRegionsDetected?.Invoke(this, regions);
        }

        if (regions.Count == 0)
            return "POI extraction found 0 regions — nothing to save.";

        await SaveDebugPoiAsync(frame, regions, settings, "Test POI (no AI)", CancellationToken.None);
        return $"Test POI: {regions.Count} region(s) found and saved to captures folder.";
    }

    // ── Debug saving ──────────────────────────────────────────────────────

    private async Task SaveDebugPoiAsync(
        byte[] framePng,
        IReadOnlyList<PoiRegion> poiRegions,
        AppConfiguration settings,
        string poiLabel,
        CancellationToken ct)
    {
        try
        {
            var captureDir = ResolveCapturesDir(settings.CapturesDirectory);
            var timestamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseName   = $"{timestamp}_debug";

            var fullPath = Path.Combine(captureDir, $"{baseName}.png");
            await File.WriteAllBytesAsync(fullPath, framePng, ct);
            _logger.LogInformation("Debug frame saved: {Path}", fullPath);

            foreach (var poi in poiRegions)
            {
                var labeled = await CaptureStorageService.LabelCropJpeg(poi.CroppedJpeg, $"POI {poi.Index} — {poiLabel}");
                var poiPath = Path.Combine(captureDir, $"{baseName}_poi_{poi.Index}.jpg");
                await File.WriteAllBytesAsync(poiPath, labeled, ct);
            }
            if (poiRegions.Count > 0)
                _logger.LogInformation("Debug: saved {Count} POI crop(s) for {Base}", poiRegions.Count, baseName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save debug POI files");
        }
    }

    private static string ResolveCapturesDir(string configured)
        => CaptureStorageService.ResolveCapturesDir(configured);

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetAnalyzing(bool value)
    {
        IsAnalyzing = value;
        IsAnalyzingChanged?.Invoke(this, value);
    }

    private void FireDaylightWindowChanged(bool allowed)
    {
        if (_wasInDaylightWindow == allowed) return; // no state change — don't spam
        _wasInDaylightWindow = allowed;
        DaylightWindowChanged?.Invoke(this, allowed);
    }
}
