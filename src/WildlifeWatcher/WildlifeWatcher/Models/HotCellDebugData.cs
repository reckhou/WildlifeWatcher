namespace WildlifeWatcher.Models;

/// <summary>
/// Debug snapshot of the hot-cell grid computed during POI extraction.
/// Each cell stores a state byte: 0 = cold, 1 = foreground only, 2 = temporal only, 3 = both (hot).
/// </summary>
public class HotCellDebugData
{
    public int GridCols { get; init; }
    public int GridRows { get; init; }
    /// <summary>
    /// Per-cell state: 0 = cold, 1 = foreground diff only (no temporal motion),
    /// 2 = temporal motion only (no foreground diff), 3 = both conditions met (hot).
    /// </summary>
    public byte[,] CellState { get; init; } = null!;
}
