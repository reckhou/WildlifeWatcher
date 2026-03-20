using WildlifeWatcher.Models;

namespace WildlifeWatcher.ViewModels;

/// <summary>Snapshot view-model for a species card. Not observable — rebuilt on each gallery refresh.</summary>
public class SpeciesCardViewModel
{
    public Species Species          { get; }
    public int     CaptureCount     { get; }
    public string  PreviewImagePath { get; }
    public string  FirstSeenLabel   { get; }
    public string  LastSeenLabel    { get; }
    public DateTime LatestCaptureAt { get; }

    public SpeciesCardViewModel(Species species)
    {
        Species      = species;
        CaptureCount = species.Captures.Count;

        var latest       = species.Captures.OrderByDescending(c => c.CapturedAt).FirstOrDefault();
        LatestCaptureAt  = latest?.CapturedAt ?? species.FirstDetectedAt;

        // Prefer iNaturalist reference photo; fall back to latest capture image
        PreviewImagePath = !string.IsNullOrEmpty(species.ReferencePhotoPath)
            ? species.ReferencePhotoPath
            : latest?.ImageFilePath ?? string.Empty;

        FirstSeenLabel = $"First seen: {species.FirstDetectedAt:d MMM yyyy}";

        var daysDiff = (DateTime.Now.Date - LatestCaptureAt.Date).TotalDays;
        LastSeenLabel = daysDiff == 0 ? "Last seen: today"
                      : daysDiff == 1 ? "Last seen: yesterday"
                      : $"Last seen: {LatestCaptureAt:d MMM}";
    }
}
