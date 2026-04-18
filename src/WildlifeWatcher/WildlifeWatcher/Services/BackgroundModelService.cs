using System.IO;
using SkiaSharp;
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
    // File format v4: magic (4) + version (1) + W (4) + H (4) + savedAtTicks (8) + frameCount (4) + floats (W*H*4)
    private const int    FileMagic   = 0x57424D47; // "WBMG"
    private const byte   FileVersion = 4;
    private const double StaleHours  = 24.0;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WildlifeWatcher", "background_model.bin");

    private readonly ISettingsService _settings;
    private readonly object _lock = new();

    private int _width;
    private int _height;
    private float[]? _background;
    private float[]? _foreground;
    private byte[]? _previousGray;
    private float[]? _temporalDelta;
    private int _frameCount;
    private bool _skipGate;

    public DateTime? SavedAt { get; private set; }

    public float[]? Foreground => _foreground;
    public float[]? TemporalDelta => _temporalDelta;
    public int Width  => _width;
    public int Height => _height;

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

    public void Initialize(int downscaleWidth, int downscaleHeight)
    {
        lock (_lock)
        {
            _width  = downscaleWidth;
            _height = downscaleHeight;
            _background    = null;
            _foreground    = new float[_width * _height];
            _temporalDelta = new float[_width * _height];
            _previousGray  = null;
            _frameCount    = 0;
            _skipGate      = false;
            SavedAt        = null;
        }
    }

    public void UpdateBackground(byte[] pngFrame)
    {
        var gray  = ToGrayPixels(pngFrame, _width, _height);
        var alpha = _settings.CurrentSettings.MotionBackgroundAlpha;

        lock (_lock)
        {
            if (_background == null)
            {
                _background = Array.ConvertAll(gray, b => (float)b);
                _previousGray = gray;
                return;
            }

            for (int i = 0; i < gray.Length; i++)
                _background[i] = (float)(alpha * gray[i] + (1 - alpha) * _background[i]);

            _previousGray = gray;
        }

        _frameCount++;
        SavedAt = DateTime.UtcNow;
        TrainingProgressChanged?.Invoke(this, TrainingProgress);
    }

    public (float[] foreground, float[] temporalDelta, byte[] grayPixels) ComputeForeground(byte[] pngFrame, byte[]? previousGray)
    {
        var gray = ToGrayPixels(pngFrame, _width, _height);
        int len  = gray.Length;
        var fg   = new float[len];
        var td   = new float[len];

        float[] bgSnap;
        lock (_lock)
        {
            if (_background == null)
                return (fg, td, gray);

            bgSnap = new float[len];
            Array.Copy(_background, bgSnap, len);
        }

        for (int i = 0; i < len; i++)
        {
            fg[i] = Math.Abs(gray[i] - bgSnap[i]);
            td[i] = previousGray != null ? Math.Abs(gray[i] - previousGray[i]) : 0f;
        }

        return (fg, td, gray);
    }

    [Obsolete("Use UpdateBackground and ComputeForeground separately.")]
    public void ProcessFrame(byte[] pngFrame)
    {
        var gray  = ToGrayPixels(pngFrame, _width, _height);
        var alpha = _settings.CurrentSettings.MotionBackgroundAlpha;

        lock (_lock)
        {
            if (_background == null)
            {
                _background    = Array.ConvertAll(gray, b => (float)b);
                _foreground    = new float[_width * _height];
                _temporalDelta = new float[_width * _height];
                _previousGray  = gray;
                return;
            }

            for (int i = 0; i < gray.Length; i++)
            {
                _foreground![i]    = Math.Abs(gray[i] - _background[i]);
                _temporalDelta![i] = _previousGray != null
                    ? Math.Abs(gray[i] - _previousGray[i])
                    : 0f;
                _background[i] = (float)(alpha * gray[i] + (1 - alpha) * _background[i]);
            }

            _previousGray = gray;
        }

        _frameCount++;
        SavedAt = DateTime.UtcNow;
        TrainingProgressChanged?.Invoke(this, TrainingProgress);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _background    = null;
            _foreground    = null;
            _previousGray  = null;
            _temporalDelta = null;
            _frameCount    = 0;
            _skipGate      = false;
            SavedAt        = null;
        }
    }

    public void SaveState()
    {
        lock (_lock)
        {
            if (_background == null || _width == 0 || _height == 0) return;

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            using var fs = File.Open(StatePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            bw.Write(FileMagic);
            bw.Write(FileVersion);
            bw.Write(_width);
            bw.Write(_height);
            var now = DateTime.UtcNow;
            bw.Write(now.Ticks);
            bw.Write(_frameCount);
            foreach (var f in _background)
                bw.Write(f);

            SavedAt = now;
        }
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

            int savedW = br.ReadInt32();
            int savedH = br.ReadInt32();

            // Reject files with mismatched dimensions
            if (_width > 0 && _height > 0 && (savedW != _width || savedH != _height))
                return false;

            var savedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
            if (DateTime.UtcNow - savedAt > TimeSpan.FromHours(StaleHours))
                return false; // stale — cold-start instead

            int savedFrameCount = br.ReadInt32();

            var bg = new float[savedW * savedH];
            for (int i = 0; i < bg.Length; i++)
                bg[i] = br.ReadSingle();

            lock (_lock)
            {
                _width         = savedW;
                _height        = savedH;
                _background    = bg;
                _foreground    = new float[savedW * savedH];
                _temporalDelta = new float[savedW * savedH];
                _previousGray  = null;
                _frameCount    = savedFrameCount;
                _skipGate      = true;
                SavedAt        = savedAt;
            }
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

    // SkiaSharp — avoids WPF's BitmapDecoder/BitmapFrame path, which creates a
    // Dispatcher (and therefore a hidden HWND) on every thread-pool worker that
    // calls it. Those HWNDs never release, and over hours of background-thread
    // ticks the process exhausts its 10k USER-handle quota, after which every
    // call throws Win32Exception (8) "Not enough memory resources".
    private static byte[] ToGrayPixels(byte[] pngBytes, int w, int h)
    {
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("BackgroundModelService not initialized — call Initialize() first.");

        using var source = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidOperationException("Failed to decode frame PNG");

        // Normalise + resize into a known Bgra8888 buffer in one pass, so the
        // luma loop below doesn't need to second-guess Skia's colour layout.
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var scaled = new SKBitmap(info);
        if (!source.ScalePixels(scaled, SKSamplingOptions.Default))
            throw new InvalidOperationException("Failed to scale frame");

        // BT.601 luma (integer form): Y = (299·R + 587·G + 114·B) / 1000.
        // Bgra8888 layout: bytes are B, G, R, A per pixel.
        var src    = scaled.Bytes;
        int stride = scaled.RowBytes;
        var result = new byte[w * h];
        for (int y = 0, i = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++, i++)
            {
                int p = row + x * 4;
                byte b = src[p];
                byte g = src[p + 1];
                byte r = src[p + 2];
                result[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            }
        }
        return result;
    }
}
