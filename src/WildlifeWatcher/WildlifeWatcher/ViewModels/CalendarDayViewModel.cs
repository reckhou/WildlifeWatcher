namespace WildlifeWatcher.ViewModels;

public class CalendarDayViewModel
{
    public DateTime? Date         { get; }
    public int       DayNumber    { get; }
    public int       CaptureCount { get; }
    public string    HeatColor    { get; }
    public bool      IsToday      { get; }
    public bool      IsBlank      { get; }

    public CalendarDayViewModel(DateTime date, int count)
    {
        Date         = date;
        DayNumber    = date.Day;
        CaptureCount = count;
        IsToday      = date.Date == DateTime.Today;
        IsBlank      = false;
        HeatColor    = count == 0  ? "#F5F5F5"
                     : count <= 2  ? "#C8E6C9"
                     : count <= 5  ? "#66BB6A"
                     :               "#2E7D32";
    }

    private CalendarDayViewModel()
    {
        IsBlank   = true;
        HeatColor = "#F5F5F5";
    }

    public static CalendarDayViewModel Blank() => new();
}
