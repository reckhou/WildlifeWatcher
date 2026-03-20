using CommunityToolkit.Mvvm.ComponentModel;
using WildlifeWatcher.Models;

namespace WildlifeWatcher.ViewModels;

public partial class MotionZoneItem : ObservableObject
{
    public int    Index       { get; init; }
    public double NLeft       { get; init; }
    public double NTop        { get; init; }
    public double NWidth      { get; init; }
    public double NHeight     { get; init; }

    [ObservableProperty] private double _canvasLeft;
    [ObservableProperty] private double _canvasTop;
    [ObservableProperty] private double _canvasWidth;
    [ObservableProperty] private double _canvasHeight;

    public static MotionZoneItem From(MotionZone z, int index, double cw, double ch) => new()
    {
        Index        = index,
        NLeft        = z.NLeft,  NTop     = z.NTop,
        NWidth       = z.NWidth, NHeight  = z.NHeight,
        CanvasLeft   = z.NLeft   * cw,  CanvasTop    = z.NTop    * ch,
        CanvasWidth  = z.NWidth  * cw,  CanvasHeight = z.NHeight * ch,
    };

    public MotionZone ToMotionZone() => new(NLeft, NTop, NWidth, NHeight);
}
