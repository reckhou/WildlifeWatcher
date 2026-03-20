namespace WildlifeWatcher.Services.Interfaces;

public interface IBirdPhotoService
{
    Task<string?> FetchAndCachePhotoAsync(string scientificName, CancellationToken ct = default);
}
