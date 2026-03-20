using LibVLCSharp.Shared;

namespace WildlifeWatcher.Services.Interfaces;

public interface ICameraService : IDisposable
{
    bool IsConnected { get; }
    MediaPlayer MediaPlayer { get; }
    event EventHandler<bool> ConnectionStateChanged;
    Task ConnectAsync();
    /// <summary>Opens a local video file for playback (debug/testing).</summary>
    Task ConnectToFileAsync(string filePath);
    Task DisconnectAsync();
    /// <summary>Tests a connection. Returns null on success, or an error description on failure.</summary>
    Task<string?> TestConnectionAsync(string url, string? username = null, string? password = null);
    Task<byte[]?> ExtractFrameAsync();
}
