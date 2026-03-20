using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

/// <summary>
/// Maintains an Exponential Moving Average (EMA) background model per pixel.
/// Any pixel that consistently differs from its history is treated as foreground.
/// The model can be persisted to disk and reloaded on next startup to skip the
/// adaptation period.
/// </summary>
public class BackgroundModelService : IBackgroundModelService
{
    private const int W = 160;
    private const int H = 120;

    // File format v3: magic (4) + version (1) + W (4) + H (4) + savedAtTicks (8) + frameCount (4) + floats (W*H*4)
    private const int    FileMagic   = 0x57424D47; // "WBMG"
    private const byte   FileVersion = 3;
    private const double StaleHours  = 2.0;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WildlifeWatcher", "background_model.bin");

    private readonly ISettingsService _settings;
    private float[]? _background;
    private float[]? _foreground;
    private int _frameCount;
    private bool _skipGate;

    public DateTime? SavedAt { get; private set; }

    public float[]? Foreground => _foreground;
    public int Width  => W;
    public int Height => H;

    public int FrameCount => _frameCount;

    public int TrainingFramesNeeded =>
        (int)Math.Ceiling(Math.Log(0.05) / Math.Log(1 - _settings.CurrentSettings.MotionBackgroundAlpha));

    public double TrainingProgress =>
        Math.Min(_frameCount / (double)TrainingFramesNeeded, 1.0);

    public bool IsTrainingComplete => _skipGate || _frameCount >= TrainingFramesNeeded;

    public event EventHandler<double>? TrainingProgressChanged;

    public BackgroundModelService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void ProcessFrame(byte[] pngFrame)
    {
        var gray  = ToGrayPixels(pngFrame);
        var alpha = _settings.CurrentSettings.MotionBackgroundAlpha;

        if (_background == null)
        {
            _background = Array.ConvertAll(gray, b => (float)b);
            _foreground = new float[W * H];
            return;
        }

        for (int i = 0; i < gray.Length; i++)
        {
            _foreground![i] = Math.Abs(gray[i] - _background[i]);
            _background[i]  = (float)(alpha * gray[i] + (1 - alpha) * _background[i]);
        }

        _frameCount++;
        SavedAt = DateTime.UtcNow;
        TrainingProgressChanged?.Invoke(this, TrainingProgress);
    }

    public void Reset()
    {
        _background = null;
        _foreground = null;
        _frameCount = 0;
        _skipGate   = false;
        SavedAt     = null;
    }

    public void SaveState()
    {
        if (_background == null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        using var fs = File.Open(StatePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(FileMagic);
        bw.Write(FileVersion);
        bw.Write(W);
        bw.Write(H);
        var now = DateTime.UtcNow;
        bw.Write(now.Ticks);
        bw.Write(_frameCount);
        foreach (var f in _background)
            bw.Write(f);

        SavedAt = now;
    }

    public bool LoadState()
    {
        if (!File.Exists(StatePath)) return false;

        try
        {
            using var fs = File.OpenRead(StatePath);
            using var br = new BinaryReader(fs);

            if (br.ReadInt32() != FileMagic)   return false;
            if (br.ReadByte()  != FileVersion) return false;
            if (br.ReadInt32() != W)           return false;
            if (br.ReadInt32() != H)           return false;

            var savedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
            if (DateTime.UtcNow - savedAt > TimeSpan.FromHours(StaleHours))
                return false; // stale — cold-start instead

            int savedFrameCount = br.ReadInt32();

            var bg = new float[W * H];
            for (int i = 0; i < bg.Length; i++)
                bg[i] = br.ReadSingle();

            _background = bg;
            _foreground = new float[W * H];
            _frameCount = savedFrameCount;
            _skipGate   = true; // data is fresh enough — skip the adaptation wait
            SavedAt     = savedAt;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void DeleteSavedState()
    {
        if (File.Exists(StatePath))
            File.Delete(StatePath);
    }

    public void SkipTraining()
    {
        _skipGate = true;
        TrainingProgressChanged?.Invoke(this, TrainingProgress);
    }

    private static byte[] ToGrayPixels(byte[] pngBytes)
    {
        using var pngStream = new MemoryStream(pngBytes);
        var source = BitmapFrame.Create(
            pngStream,
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);

        var scaled = new TransformedBitmap(source,
            new ScaleTransform(
                (double)W / source.PixelWidth,
                (double)H / source.PixelHeight));

        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var pixels = new byte[W * H];
        gray.CopyPixels(pixels, W, 0);
        return pixels;
    }
}
