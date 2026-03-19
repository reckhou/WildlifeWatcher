namespace WildlifeWatcher.Services.Interfaces;

public interface ICameraService : IDisposable
{
    bool IsConnected { get; }
    event EventHandler<bool> ConnectionStateChanged;
    Task ConnectAsync();
    Task DisconnectAsync();
    Task<bool> TestConnectionAsync();
    Task<byte[]?> ExtractFrameAsync();
}
