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

    public SpeciesCardViewModel(Species species)
    {
        Species      = species;
        CaptureCount = species.Captures.Count;

        var latest = species.Captures.OrderByDescending(c => c.CapturedAt).FirstOrDefault();
        PreviewImagePath = latest?.ImageFilePath ?? string.Empty;
        FirstSeenLabel   = $"First seen: {species.FirstDetectedAt:d MMM yyyy}";

        var lastAt   = latest?.CapturedAt ?? species.FirstDetectedAt;
        var daysDiff = (DateTime.Now.Date - lastAt.Date).TotalDays;
        LastSeenLabel = daysDiff == 0 ? "Last seen: today"
                      : daysDiff == 1 ? "Last seen: yesterday"
                      : $"Last seen: {lastAt:d MMM}";
    }
}
