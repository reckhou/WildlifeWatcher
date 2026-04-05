namespace WildlifeWatcher.Models;

public record MotionZone(double NLeft, double NTop, double NWidth, double NHeight, bool ForegroundOnly = false);
