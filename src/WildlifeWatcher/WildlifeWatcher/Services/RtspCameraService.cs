using System.Collections.Concurrent;
using System.IO;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class RtspCameraService : ICameraService
{
    private readonly ISettingsService _settings;
    private readonly ICredentialService _credentials;
    private readonly ILogger<RtspCameraService> _logger;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly ConcurrentQueue<string> _recentVlcMessages = new();
    private bool _disposed;
    private bool _isFileMode;
    private string? _currentFilePath;
    private volatile bool _suppressEvents; // true while restarting a file loop

    public bool IsConnected { get; private set; }
    public MediaPlayer MediaPlayer => _mediaPlayer;
    public event EventHandler<bool>? ConnectionStateChanged;

    public RtspCameraService(
        ISettingsService settings,
        ICredentialService credentials,
        ILogger<RtspCameraService> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _logger = logger;

        Core.Initialize();
        _libVlc = new LibVLC("--verbose=1", "--no-osd", "--no-snapshot-preview");

        // Forward all LibVLC log messages into Serilog + rolling buffer
        _libVlc.Log += OnLibVlcLog;

        _mediaPlayer = new MediaPlayer(_libVlc);

        _mediaPlayer.Playing += (_, _) =>
        {
            if (_suppressEvents) { _suppressEvents = false; return; } // loop restart — ignore
            IsConnected = true;
            ConnectionStateChanged?.Invoke(this, true);
        };
        _mediaPlayer.Stopped += (_, _) =>
        {
            if (_suppressEvents) return; // loop restart — ignore
            IsConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
        };
        _mediaPlayer.EncounteredError += (_, _) =>
        {
            _suppressEvents = false;
            IsConnected = false;
            _logger.LogWarning("RTSP stream encountered an error — see VLC log lines above for detail");
            ConnectionStateChanged?.Invoke(this, false);
        };
        // Loop file playback: when the file ends, replay it without triggering disconnect
        _mediaPlayer.EndReached += (_, _) =>
        {
            if (!_isFileMode || _currentFilePath == null) return;
            _suppressEvents = true; // suppress the Stopped that follows EndReached
            Task.Run(() =>
            {
                var uri   = new Uri(_currentFilePath).AbsoluteUri;
                var media = new Media(_libVlc, uri, FromType.FromLocation);
                _mediaPlayer.Media = media;
                _mediaPlayer.Play();
            });
        };
    }

    public Task ConnectAsync()
    {
        _isFileMode = false;
        _currentFilePath = null;
        var url = BuildUrl();
        _logger.LogInformation("Connecting to {Url}", MaskCredentials(url));
        var media = new Media(_libVlc, url, FromType.FromLocation);
        _mediaPlayer.Media = media;
        _mediaPlayer.Play();
        return Task.CompletedTask;
    }

    public Task ConnectToFileAsync(string filePath)
    {
        _isFileMode = true;
        _currentFilePath = filePath;
        _logger.LogInformation("Opening local video file: {Path}", filePath);
        var uri   = new Uri(filePath).AbsoluteUri;
        var media = new Media(_libVlc, uri, FromType.FromLocation);
        _mediaPlayer.Media = media;
        _mediaPlayer.Play();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _isFileMode = false;
        _currentFilePath = null;
        _suppressEvents = false;
        _mediaPlayer.Stop();
        return Task.CompletedTask;
    }

    public async Task<string?> TestConnectionAsync(string url, string? username = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL is empty. Enter an RTSP address in Settings first.";

        if (!string.IsNullOrEmpty(username) && url.StartsWith("rtsp://"))
        {
            var user = Uri.EscapeDataString(username);
            var pass = Uri.EscapeDataString(password ?? string.Empty);
            url = $"rtsp://{user}:{pass}@{url[7..]}";
        }

        _logger.LogInformation("Testing connection to {Url}", MaskCredentials(url));

        // Collect VLC messages that arrive during this test
        var testMessages = new List<string>();

        using var testPlayer = new MediaPlayer(_libVlc);
        var tcs = new TaskCompletionSource<bool>();

        void OnMessage(object? s, LogEventArgs e)
        {
            if (e.Level >= LibVLCSharp.Shared.LogLevel.Warning)
                testMessages.Add($"[{e.Level}] {e.Message}");
        }

        _libVlc.Log += OnMessage;

        testPlayer.Playing += (_, _) => tcs.TrySetResult(true);
        testPlayer.EncounteredError += (_, _) => tcs.TrySetResult(false);

        var media = new Media(_libVlc, url, FromType.FromLocation);
        testPlayer.Media = media;
        testPlayer.Play();

        await Task.WhenAny(tcs.Task, Task.Delay(8000));
        var success = tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;

        testPlayer.Stop();
        _libVlc.Log -= OnMessage;

        if (success) return null;

        // Build a meaningful failure message from VLC's own output
        var detail = testMessages.Count > 0
            ? string.Join(" | ", testMessages.TakeLast(3))
            : "Connection timed out or was refused. Check the IP, port, and credentials.";

        _logger.LogWarning("Test connection failed: {Detail}", detail);
        return detail;
    }

    public async Task<byte[]?> ExtractFrameAsync()
    {
        if (!IsConnected) return null;

        var tempFile = Path.Combine(Path.GetTempPath(), $"ww_frame_{Guid.NewGuid():N}.png");
        try
        {
            var success = await Task.Run(() => _mediaPlayer.TakeSnapshot(0, tempFile, 0, 0));
            if (!success || !File.Exists(tempFile)) return null;
            return await File.ReadAllBytesAsync(tempFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract frame");
            return null;
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private void OnLibVlcLog(object? sender, LogEventArgs e)
    {
        // Suppress known benign VLC internal probes that produce noisy warnings
        if (e.Message != null && e.Message.Contains("unsupported control query"))
            return;

        // Keep a rolling window of recent messages for error reporting
        _recentVlcMessages.Enqueue($"[{e.Level}] {e.Message}");
        while (_recentVlcMessages.Count > 50)
            _recentVlcMessages.TryDequeue(out _);

        // Forward to Serilog at the appropriate level
        switch (e.Level)
        {
            case LibVLCSharp.Shared.LogLevel.Debug:
                _logger.LogDebug("[VLC] {Message}", e.Message);
                break;
            case LibVLCSharp.Shared.LogLevel.Notice:
                _logger.LogInformation("[VLC] {Message}", e.Message);
                break;
            case LibVLCSharp.Shared.LogLevel.Warning:
                _logger.LogWarning("[VLC] {Message}", e.Message);
                break;
            case LibVLCSharp.Shared.LogLevel.Error:
                _logger.LogError("[VLC] {Message}", e.Message);
                break;
        }
    }

    private string BuildUrl()
    {
        var url = _settings.CurrentSettings.RtspUrl;
        var creds = _credentials.LoadCredentials();
        if (creds != null && !string.IsNullOrEmpty(creds.RtspUsername) && url.StartsWith("rtsp://"))
        {
            var user = Uri.EscapeDataString(creds.RtspUsername);
            var pass = Uri.EscapeDataString(creds.RtspPassword);
            url = $"rtsp://{user}:{pass}@{url[7..]}";
        }
        return url;
    }

    private static string MaskCredentials(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, @"rtsp://([^:@]+):([^@]+)@");
        return match.Success ? url.Replace(match.Groups[2].Value, "***") : url;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _libVlc.Log -= OnLibVlcLog;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
