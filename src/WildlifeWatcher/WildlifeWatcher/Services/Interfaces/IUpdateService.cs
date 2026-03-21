using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services.Interfaces;

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(bool skipDebugOverride = false, CancellationToken ct = default);
    Task ApplyUpdateAsync(UpdateInfo update, IProgress<int> progress, CancellationToken ct = default);
}
