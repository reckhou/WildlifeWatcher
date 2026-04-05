using System.Diagnostics;
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
    // Cancelled whenever the camera disconnects, reset on each disconnect so the
    // next connection gets a fresh token. Used to abort in-progress AI calls.
    private CancellationTokenSource _cameraConnectedCts = new();
    // Cancelled whenever settings change so the main loop's Task.Delay exits early
    // and re-reads the interval immediately.
    private CancellationTokenSource _loopWakeCts = new();
    private DateTime _cooldownUntil = DateTime.MinValue;
    private bool _wasInDaylightWindow = true;

    // Track previous gray pixels for temporal delta in the detection tick
    private byte[]? _tickPreviousGray;

    // Track camera resolution for dynamic grid initialization
    private int _cameraWidth;
    private int _cameraHeight;
    private int _lastCellSize;

    public bool IsRunning   { get; private set; }
    public bool IsAnalyzing { get; private set; }

    public event EventHandler<DetectionEvent>?               DetectionOccurred;
    public event EventHandler<bool>?                         IsAnalyzingChanged;
    public event EventHandler<IReadOnlyList<PoiRegion>>?     PoiRegionsDetected;
    public event EventHandler<bool>?                         DaylightWindowChanged;
    public event EventHandler<(int completed, int total)>?   BurstProgressChanged;
    public event EventHandler<HotCellDebugData?>?            HotCellDebugComputed;

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
        _camera.ConnectionStateChanged += OnCameraConnectionChanged;
    }

    private void OnSettingsChanged(object? sender, AppConfiguration settings)
    {
        // Wake the main loop so it re-reads the new interval immediately.
        var old = Interlocked.Exchange(ref _loopWakeCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();

        bool allowed = _daylightWindow.IsDetectionAllowed(settings);
        FireDaylightWindowChanged(allowed);
    }

    private void OnCameraConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
        {
            // Atomically swap so no thread can Cancel/Dispose the same instance twice.
            var old = Interlocked.Exchange(ref _cameraConnectedCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IsRunning = true;
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        _ = Task.Run(() => RunBackgroundUpdateLoopAsync(_cts.Token), _cts.Token);
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
        _camera.ConnectionStateChanged -= OnCameraConnectionChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        var cts = Interlocked.Exchange(ref _cameraConnectedCts, null!);
        cts?.Cancel();
        cts?.Dispose();
        var wake = Interlocked.Exchange(ref _loopWakeCts, null!);
        wake?.Cancel();
        wake?.Dispose();
    }

    // ── Standalone background update loop ─────────────────────────────────

    private async Task RunBackgroundUpdateLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var intervalSeconds = _settings.CurrentSettings.BackgroundUpdateIntervalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1, 30)), ct);
                if (ct.IsCancellationRequested || !_camera.IsConnected) continue;

                try
                {
                    var frame = await _camera.ExtractFrameAsync();
                    if (frame == null) continue;

                    EnsureInitialized(frame);
                    _background.UpdateBackground(frame);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background update tick failed");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in background update loop");
        }
    }

    /// <summary>
    /// Decode frame dimensions and initialize background + POI services if not yet done
    /// or if the camera resolution / cell size changed.
    /// </summary>
    private void EnsureInitialized(byte[] pngFrame)
    {
        var settings = _settings.CurrentSettings;
        int cellSize = settings.PoiCellSizePixels;

        // Decode frame dimensions (cheap — just reads header)
        int frameW, frameH;
        using (var ms = new System.IO.MemoryStream(pngFrame))
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                ms, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            frameW = frame.PixelWidth;
            frameH = frame.PixelHeight;
        }

        if (frameW == _cameraWidth && frameH == _cameraHeight && cellSize == _lastCellSize)
            return; // already initialized with same params

        _cameraWidth  = frameW;
        _cameraHeight = frameH;
        _lastCellSize = cellSize;

        var (gridCols, gridRows, downscaleW, downscaleH) =
            PointOfInterestService.ComputeGridDimensions(frameW, frameH, cellSize);

        _poi.Initialize(gridCols, gridRows);

        // If the background model was already loaded from disk (e.g. by
        // LiveViewModel.OnConnectionStateChanged) with matching dimensions,
        // preserve it instead of wiping and reloading.
        if (_background.Width == downscaleW && _background.Height == downscaleH && _background.FrameCount > 0)
        {
            _tickPreviousGray = null;
            _logger.LogInformation(
                "Initialized POI grid: camera {CamW}×{CamH}, cell size {Cell}px → grid {Cols}×{Rows} (background model already loaded, frame count {Count})",
                frameW, frameH, cellSize, gridCols, gridRows, _background.FrameCount);
            return;
        }

        _background.Initialize(downscaleW, downscaleH);
        _tickPreviousGray = null;

        _logger.LogInformation(
            "Initialized: camera {CamW}×{CamH}, cell size {Cell}px → grid {Cols}×{Rows}, downscale {DsW}×{DsH}",
            frameW, frameH, cellSize, gridCols, gridRows, downscaleW, downscaleH);

        // Try to restore persisted background model (may fail if dimensions changed)
        if (_background.LoadState())
            _logger.LogInformation("Background model restored from disk (frame count {Count})", _background.FrameCount);
    }

    // ── Main loop ─────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var interval = TimeSpan.FromSeconds(_settings.CurrentSettings.FrameExtractionIntervalSeconds);

                // Link with _loopWakeCts so a settings change interrupts the delay
                // and the loop re-reads the new interval immediately.
                var wakeCts = Interlocked.CompareExchange(ref _loopWakeCts, null!, null!);
                if (wakeCts is null) break; // disposed — shutting down
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wakeCts.Token);
                try { await Task.Delay(interval, linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }

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

        // Clear overlays at the start of each tick
        PoiRegionsDetected?.Invoke(this, Array.Empty<PoiRegion>());
        if (!settings.ShowHotCellDebugOverlay)
            HotCellDebugComputed?.Invoke(this, null);

        // ── Ensure services are initialized ─────────────────────────────
        EnsureInitialized(currentFrame);

        // ── Compute foreground against current background ────────────────
        var (fg, temporalDelta, grayPixels) = _background.ComputeForeground(currentFrame, _tickPreviousGray);
        _tickPreviousGray = grayPixels;

        if (_background.FrameCount == 0) return; // background not yet seeded

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
                temporalDelta, settings.MotionTemporalThreshold, settings.MotionTemporalCellFraction, settings.MaxPoiRegions);
            _logger.LogInformation("POI extraction: {Count} region(s) found", poiRegions.Count);

            if (settings.ShowHotCellDebugOverlay)
                HotCellDebugComputed?.Invoke(this, _poi.LastHotCellDebug);

            if (poiRegions.Count == 0)
            {
                _logger.LogInformation("POI extraction found 0 regions — skipping AI call");
                PoiRegionsDetected?.Invoke(this, poiRegions);
                return;
            }

            // ── Burst capture ────────────────────────────────────────────
            if (settings.EnableBurstCapture && poiRegions.Count > 0)
            {
                var burstResult = await RunBurstCaptureAsync(currentFrame, settings, zones, ct);
                if (burstResult.regions.Count > 0)
                {
                    poiRegions = burstResult.regions;
                    currentFrame = burstResult.bestFrame ?? currentFrame;
                }
            }

            PoiRegionsDetected?.Invoke(this, poiRegions);
        }

        // ── Cooldown check ───────────────────────────────────────────────
        if (DateTime.UtcNow < _cooldownUntil)
        {
            var remaining = (_cooldownUntil - DateTime.UtcNow).TotalSeconds;
            _logger.LogInformation("Cooldown active — skipping AI ({Remaining:F0}s remaining)", remaining);
            return;
        }

        // ── Guard: abort if camera disconnected during burst/POI ────────────
        if (!_camera.IsConnected) return;

        // ── AI recognition ───────────────────────────────────────────────
        // Link the service token with the per-connection token so an RTSP
        // disconnection mid-call causes RecognizeAsync to return promptly.
        var localDisconnectCts = Interlocked.CompareExchange(ref _cameraConnectedCts, null!, null!);
        if (localDisconnectCts is null) return; // disposed — service is shutting down
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, localDisconnectCts.Token);

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

            var results = await _ai.RecognizeAsync(currentFrame, poiJpegs, linkedCts.Token);

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

                    CaptureRecord? savedRecord = null;
                    try { savedRecord = await _captureStorage.SaveCaptureAsync(currentFrame, result, poiRegions, batchStartedAt); }
                    catch (Exception ex) { _logger.LogError(ex, "Failed to save capture"); }

                    // Only surface the detection if it was actually persisted — prevents
                    // recent-detection items that have no matching DB record when clicked.
                    if (savedRecord is null) continue;

                    var evt = new DetectionEvent
                    {
                        DetectedAt  = DateTime.Now,
                        Result      = result,
                        FramePng    = currentFrame,
                        PoiRegions  = poiRegions,
                        SavedRecord = savedRecord
                    };
                    DetectionOccurred?.Invoke(this, evt);
                    _logger.LogInformation(
                        "✓ Detection: {Species} ({ScientificName}) — confidence {Confidence:P0}",
                        result.CommonName, result.ScientificName, result.Confidence);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by camera disconnection, not service shutdown — return quietly.
            _logger.LogInformation("AI recognition interrupted — camera disconnected mid-call");
        }
        finally
        {
            SetAnalyzing(false);
        }
    }

    // ── Burst capture ────────────────────────────────────────────────────

    private async Task<(IReadOnlyList<PoiRegion> regions, byte[]? bestFrame)> RunBurstCaptureAsync(
        byte[] triggerFrame, AppConfiguration settings,
        IReadOnlyList<MotionZone>? zones, CancellationToken ct)
    {
        var poiSvc = (PointOfInterestService)_poi;
        int burstCount = Math.Clamp(settings.BurstFrameCount, 3, 30);
        int intervalMs = Math.Clamp(settings.BurstIntervalMs, 200, 3000);

        // Get grid dimensions for heatmap
        var (gridCols, gridRows, _, _) = PointOfInterestService.ComputeGridDimensions(
            _cameraWidth, _cameraHeight, settings.PoiCellSizePixels);
        var heatmap = new float[gridRows, gridCols];

        byte[]? burstPreviousGray = null;
        var burstFrames = new List<(byte[] frame, float[,] hotCells)>(burstCount);

        _logger.LogInformation("Burst capture starting: {Count} frames at {Interval}ms intervals",
            burstCount, intervalMs);

        BurstProgressChanged?.Invoke(this, (0, burstCount));

        // Wall-clock scheduling: each frame targets (i+1)*intervalMs from burst start.
        // This absorbs extraction + processing time into the interval rather than
        // adding it on top (which would make a 200ms interval behave like ~500ms).
        var burstStart = Stopwatch.GetTimestamp();

        for (int i = 0; i < burstCount; i++)
        {
            long targetMs = (long)(i + 1) * intervalMs;
            long waitMs = targetMs - (long)Stopwatch.GetElapsedTime(burstStart).TotalMilliseconds;
            if (waitMs > 0)
                await Task.Delay((int)waitMs, ct);

            if (!_camera.IsConnected) break;

            var frame = await _camera.ExtractFrameAsync();
            if (frame == null) continue;

            var (burstFg, burstTd, burstGray) = _background.ComputeForeground(frame, burstPreviousGray);
            burstPreviousGray = burstGray;

            var hotCells = poiSvc.ComputeHotCellGrid(burstFg, burstTd, zones,
                settings.MotionPixelThreshold, settings.PoiSensitivity,
                settings.MotionTemporalThreshold, settings.MotionTemporalCellFraction);
            poiSvc.AccumulateHeatmap(heatmap, hotCells);
            burstFrames.Add((frame, hotCells));

            _logger.LogDebug("Burst capture: frame {Index}/{Total}", i + 1, burstCount);
            BurstProgressChanged?.Invoke(this, (i + 1, burstCount));
        }

        BurstProgressChanged?.Invoke(this, (burstCount, burstCount));

        if (burstFrames.Count == 0)
            return (Array.Empty<PoiRegion>(), null);

        var regions = new List<PoiRegion>();
        byte[]? overallBestFrame = null;
        float overallBestIntensity = 0;
        int cap = Math.Clamp(settings.MaxPoiRegions, 3, 10);

        // ── Priority: ForegroundOnly zones — direct zone crop from best burst frame ──
        if (zones is { Count: > 0 })
        {
            foreach (var zone in zones.Where(z => z.ForegroundOnly))
            {
                int zMinCol = Math.Clamp((int)(zone.NLeft * gridCols), 0, gridCols - 1);
                int zMaxCol = Math.Clamp((int)((zone.NLeft + zone.NWidth) * gridCols), 0, gridCols - 1);
                int zMinRow = Math.Clamp((int)(zone.NTop * gridRows), 0, gridRows - 1);
                int zMaxRow = Math.Clamp((int)((zone.NTop + zone.NHeight) * gridRows), 0, gridRows - 1);

                bool hasActivity = false;
                for (int r = zMinRow; r <= zMaxRow && !hasActivity; r++)
                for (int c = zMinCol; c <= zMaxCol && !hasActivity; c++)
                    if (heatmap[r, c] > 0) hasActivity = true;

                if (!hasActivity) continue;

                // Find best burst frame for this zone
                float bestIntensity = 0;
                int bestIdx = 0;
                for (int fi = 0; fi < burstFrames.Count; fi++)
                {
                    float total = 0;
                    var cells = burstFrames[fi].hotCells;
                    for (int r = zMinRow; r <= zMaxRow; r++)
                    for (int c = zMinCol; c <= zMaxCol; c++)
                        total += cells[r, c];
                    if (total > bestIntensity)
                    {
                        bestIntensity = total;
                        bestIdx = fi;
                    }
                }

                var bestFrame = burstFrames[bestIdx].frame;
                var crop = poiSvc.CropZoneDirect(bestFrame, zone, regions.Count + 1);
                if (crop != null)
                {
                    regions.Add(crop);
                    if (bestIntensity > overallBestIntensity)
                    {
                        overallBestIntensity = bestIntensity;
                        overallBestFrame = bestFrame;
                    }
                }

                // Zero out zone cells so ExtractHeatmapRegions won't double-count
                for (int r = zMinRow; r <= zMaxRow; r++)
                for (int c = zMinCol; c <= zMaxCol; c++)
                    heatmap[r, c] = 0;
            }
        }

        // ── Heatmap BFS for remaining (non-priority) regions ──
        var heatmapRegions = poiSvc.ExtractHeatmapRegions(heatmap, minHitCount: 2, settings.PoiSensitivity, settings.MaxPoiRegions);
        _logger.LogInformation("Burst complete: {PriorityCount} priority + {HeatmapCount} heatmap region(s) from {Frames} frames",
            regions.Count, heatmapRegions.Count, burstFrames.Count);

        for (int ri = 0; ri < heatmapRegions.Count && regions.Count < cap; ri++)
        {
            var (minRow, maxRow, minCol, maxCol, _) = heatmapRegions[ri];

            // Find the best burst frame for this region
            float bestIntensity = 0;
            int bestIdx = 0;
            for (int fi = 0; fi < burstFrames.Count; fi++)
            {
                float total = 0;
                var cells = burstFrames[fi].hotCells;
                for (int r = minRow; r <= maxRow; r++)
                for (int c = minCol; c <= maxCol; c++)
                    total += cells[r, c];

                if (total > bestIntensity)
                {
                    bestIntensity = total;
                    bestIdx = fi;
                }
            }

            var bestFrame = burstFrames[bestIdx].frame;
            var crop = poiSvc.CropRegion(bestFrame, minRow, maxRow, minCol, maxCol, regions.Count + 1);
            if (crop != null)
            {
                regions.Add(crop);
                if (bestIntensity > overallBestIntensity)
                {
                    overallBestIntensity = bestIntensity;
                    overallBestFrame = bestFrame;
                }
            }
        }

        return (regions, overallBestFrame);
    }

    // ── Test POI ──────────────────────────────────────────────────────────

    public async Task<string> TriggerTestPoiAsync()
    {
        if (!_camera.IsConnected)
            return "Camera not connected.";

        // Take two frames with a brief delay so the temporal delta has real data.
        // A single frame always produces zero temporal motion → 0 regions.
        var firstFrame = await _camera.ExtractFrameAsync();
        if (firstFrame == null)
            return "Failed to extract first frame.";

        var settings = _settings.CurrentSettings;
        EnsureInitialized(firstFrame);

        var (_, _, previousGray) = _background.ComputeForeground(firstFrame, null);
        if (_background.FrameCount == 0)
            return "Background model not ready yet (first frame).";

        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.BackgroundUpdateIntervalSeconds, 1, 5)));

        var frame = await _camera.ExtractFrameAsync();
        if (frame == null)
            return "Failed to extract second frame.";

        var (fg, temporalDelta, _) = _background.ComputeForeground(frame, previousGray);

        var zones = settings.MotionWhitelistZones.Count > 0
            ? (IReadOnlyList<MotionZone>)settings.MotionWhitelistZones : null;

        IReadOnlyList<PoiRegion> regions = Array.Empty<PoiRegion>();
        if (settings.EnablePoiExtraction)
        {
            regions = _poi.ExtractRegions(fg, frame, zones, settings.MotionPixelThreshold,
                settings.PoiSensitivity, temporalDelta, settings.MotionTemporalThreshold,
                settings.MotionTemporalCellFraction, settings.MaxPoiRegions);
            _logger.LogInformation("Test POI extraction: {Count} region(s) found", regions.Count);

            if (settings.ShowHotCellDebugOverlay)
                HotCellDebugComputed?.Invoke(this, _poi.LastHotCellDebug);

            // Run burst if enabled and initial POI found regions
            if (settings.EnableBurstCapture && regions.Count > 0)
            {
                var burstResult = await RunBurstCaptureAsync(frame, settings, zones, CancellationToken.None);
                if (burstResult.regions.Count > 0)
                {
                    regions = burstResult.regions;
                    frame = burstResult.bestFrame ?? frame;
                }
            }

            PoiRegionsDetected?.Invoke(this, regions);
        }

        if (regions.Count == 0)
            return "POI extraction found 0 regions — nothing to save.";

        if (!settings.SavePoiDebugImages)
            return settings.EnableBurstCapture
                ? $"Burst POI: {regions.Count} region(s) from {settings.BurstFrameCount} frames — debug image saving is disabled."
                : $"Test POI: {regions.Count} region(s) found — debug image saving is disabled.";

        string label = settings.EnableBurstCapture ? "Burst POI (no AI)" : "Test POI (no AI)";
        await SaveDebugPoiAsync(frame, regions, settings, label, CancellationToken.None);
        return settings.EnableBurstCapture
            ? $"Burst POI: {regions.Count} region(s) from {settings.BurstFrameCount} frames, saved to captures folder."
            : $"Test POI: {regions.Count} region(s) found and saved to captures folder.";
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
