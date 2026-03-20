using System.IO;
using WildlifeWatcher.Models;

namespace WildlifeWatcher.ViewModels;

public class CaptureCardViewModel
{
    public CaptureRecord Record          { get; }
    public string        ImagePath       => Record.ImageFilePath;
    public string        TimeLabel       => Record.CapturedAt.ToString("dd MMM  HH:mm:ss");
    public string        ConfidenceLabel => $"{Record.ConfidenceScore:P0}";

    public string DisplayImagePath { get; }

    public CaptureCardViewModel(CaptureRecord record)
    {
        Record = record;
        DisplayImagePath = record.AnnotatedImageFilePath is { Length: > 0 } p && File.Exists(p)
            ? p
            : record.ImageFilePath;
    }
}
