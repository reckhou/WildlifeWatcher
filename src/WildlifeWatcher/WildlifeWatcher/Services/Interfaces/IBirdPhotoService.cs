namespace WildlifeWatcher.Services.Interfaces;

public interface IBirdPhotoService
{
    /// <summary>
    /// Fetches and caches a reference photo for the given scientific name.
    /// <list type="bullet">
    ///   <item><c>Path</c> — local cache path, or null if no photo was found.</item>
    ///   <item><c>FetchedFromNetwork</c> — true when a network call was made; callers should apply a polite delay.</item>
    ///   <item><c>RateLimited</c> — true when iNaturalist returned 429; caller should queue for later retry.</item>
    /// </list>
    /// </summary>
    Task<(string? Path, bool FetchedFromNetwork, bool RateLimited)> FetchAndCachePhotoAsync(string scientificName, bool forceRefresh = false, CancellationToken ct = default);
}
