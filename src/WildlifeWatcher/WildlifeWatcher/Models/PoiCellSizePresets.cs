namespace WildlifeWatcher.Models;

public static class PoiCellSizePresets
{
    public static readonly (int Size, string Name, string Description)[] All =
    {
        (20, "Ultra Fine",  "Tiny subjects, distant animals, insects"),
        (30, "Very Fine",   "Small birds (wrens, tits), mice"),
        (40, "Fine",        "Medium birds (robins, starlings)"),
        (50, "Standard",    "Squirrels, rabbits, pigeons"),
        (60, "Moderate",    "Cats, magpies, larger birds"),
        (70, "Coarse",      "Foxes, herons, large subjects"),
        (80, "Very Coarse", "Deer, very large/close animals"),
    };
}
