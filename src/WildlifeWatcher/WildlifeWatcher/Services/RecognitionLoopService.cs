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
    private readonly ILogger<RecognitionLoopService> _logger;

    private CancellationTokenSource? _cts;
    private DateTime _cooldownUntil = DateTime.MinValue;

    public bool IsRunning   { get; private set; }
    public bool IsAnalyzing { get; private set; }
    public bool IsDebugMode { get; set; }

    public event EventHandler<DetectionEvent>?               DetectionOccurred;
    public event EventHandler<bool>?                         IsAnalyzingChanged;
    public event EventHandler<IReadOnlyList<PoiRegion>>?     PoiRegionsDetected;

    public RecognitionLoopService(
        ICameraService              camera,
        IAiRecognitionService       ai,
        IPointOfInterestService     poi,
        IBackgroundModelService     background,
        ISettingsService            settings,
        ICaptureStorageService      captureStorage,
        ILogger<RecognitionLoopService> logger)
    {
        _camera         = camera;
        _ai             = ai;
        _poi            = poi;
        _background     = background;
        _settings       = settings;
        _captureStorage = captureStorage;
        _logger         = logger;
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
        if (fg == null) return; // first frame — no background established yet

        // ── Training gate: defer POI + AI until background model is ready ─
        if (!_background.IsTrainingComplete)
        {
            _logger.LogInformation(
                "Background training {Progress:P0} ({Frames}/{Needed}) — waiting for model to stabilise",
                _background.TrainingProgress, _background.FrameCount, _background.TrainingFramesNeeded);
            return;
        }

        var zones = settings.MotionWhitelistZones.Count > 0
            ? (IReadOnlyList<MotionZone>)settings.MotionWhitelistZones : null;

        // ── POI extraction ───────────────────────────────────────────────
        IReadOnlyList<PoiRegion> poiRegions = Array.Empty<PoiRegion>();
        if (settings.EnablePoiExtraction)
        {
            poiRegions = _poi.ExtractRegions(fg, currentFrame, zones, settings.MotionPixelThreshold, settings.PoiSensitivity);
            _logger.LogInformation("POI extraction: {Count} region(s) found", poiRegions.Count);
            PoiRegionsDetected?.Invoke(this, poiRegions);

            if (poiRegions.Count == 0)
            {
                _logger.LogInformation("POI extraction found 0 regions — skipping AI call");
                return;
            }
        }

        // ── Debug mode: save POIs locally, skip AI ───────────────────────
        if (IsDebugMode)
        {
            bool inCooldown   = DateTime.UtcNow < _cooldownUntil;
            var  poiLabel     = inCooldown
                ? $"NOT sent to AI (cooldown — {(_cooldownUntil - DateTime.UtcNow).TotalSeconds:F0}s remaining)"
                : "Would be sent to AI (debug mode — AI skipped)";
            _logger.LogInformation("Debug mode: saving {Count} POI(s) [{Label}]", poiRegions.Count, poiLabel);
            await SaveDebugPoiAsync(currentFrame, poiRegions, settings, poiLabel, ct);
            return;
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
}
