namespace WildlifeWatcher.Services.Interfaces;

public interface IBirdPhotoService
{
    Task<string?> FetchAndCachePhotoAsync(string scientificName, bool forceRefresh = false, CancellationToken ct = default);
}
