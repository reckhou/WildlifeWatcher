using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.ViewModels;

/// <summary>Snapshot view-model for a species card. Not observable — rebuilt on each gallery refresh.</summary>
public class SpeciesCardViewModel
{
    public SpeciesSummary Summary         { get; }
    public int            CaptureCount    { get; }
    public string         PreviewImagePath { get; }
    public string         FirstSeenLabel  { get; }
    public string         LastSeenLabel   { get; }
    public DateTime       LatestCaptureAt { get; }

    public SpeciesCardViewModel(SpeciesSummary summary)
    {
        Summary      = summary;
        CaptureCount = summary.CaptureCount;
        LatestCaptureAt = summary.LatestCaptureAt;

        // Prefer iNaturalist reference photo; fall back to latest capture image
        PreviewImagePath = !string.IsNullOrEmpty(summary.ReferencePhotoPath)
            ? summary.ReferencePhotoPath
            : summary.LatestImagePath ?? string.Empty;

        FirstSeenLabel = $"First seen: {summary.FirstDetectedAt:d MMM yyyy}";

        var daysDiff = (DateTime.Now.Date - LatestCaptureAt.Date).TotalDays;
        LastSeenLabel = daysDiff == 0 ? "Last seen: today"
                      : daysDiff == 1 ? "Last seen: yesterday"
                      : $"Last seen: {LatestCaptureAt:d MMM}";
    }
}
