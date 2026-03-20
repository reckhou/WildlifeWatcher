using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mscc.GenerativeAI;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

public class GeminiRecognitionService : IAiRecognitionService
{
    private const string SystemPrompt =
        "You are a garden wildlife identification assistant. " +
        "This camera is mounted in a domestic garden, so the most commonly seen animals are domestic cats, dogs, and garden birds (robins, pigeons, sparrows, starlings, blackbirds, etc.). " +
        "Give strong preference to these species when the image is ambiguous — only identify as a rarer wild animal if the visual evidence clearly rules out cats, dogs, and birds. " +
        "Respond with strict JSON only — no markdown, no code fences. " +
        "Format: {\"detected\":true,\"source_crop_index\":1,\"candidates\":[{\"common_name\":\"Domestic Cat\",\"scientific_name\":\"Felis catus\",\"confidence\":0.92,\"description\":\"...\"},{\"common_name\":\"Red Fox\",\"scientific_name\":\"Vulpes vulpes\",\"confidence\":0.05,\"description\":\"...\"}]} " +
        "When multiple crop regions are sent, include \"source_crop_index\" (1-based integer) indicating which region the best match was found in. Omit source_crop_index for full-frame analysis. " +
        "List up to 3 most likely animals in descending confidence order. " +
        "If no animal is visible: {\"detected\":false,\"candidates\":[]}";

    private readonly ISettingsService _settings;
    private readonly ICredentialService _credentials;
    private readonly ILogger<GeminiRecognitionService> _logger;

    public GeminiRecognitionService(
        ISettingsService settings,
        ICredentialService credentials,
        ILogger<GeminiRecognitionService> logger)
    {
        _settings    = settings;
        _credentials = credentials;
        _logger      = logger;
    }

    public async Task<RecognitionResult> RecognizeAsync(
        byte[] fullFramePng,
        IReadOnlyList<byte[]>? poiJpegs = null,
        CancellationToken ct = default)
    {
        var apiKey = _credentials.LoadCredentials()?.GeminiApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Gemini API key configured; skipping AI recognition");
            return new RecognitionResult(false, string.Empty, string.Empty, 0, string.Empty, string.Empty, Array.Empty<SpeciesCandidate>(), SourcePoiIndex: null);
        }

        var modelName = _settings.CurrentSettings.GeminiModel;

        try
        {
            var googleAi = new GoogleAI(apiKey: apiKey);
            var model    = googleAi.GenerativeModel(
                model:             modelName,
                systemInstruction: new Content(new TextData { Text = SystemPrompt }));

            List<IPart> parts;
            if (poiJpegs is { Count: > 0 })
            {
                _logger.LogInformation("Sending {Count} POI crop(s) to Gemini ({Model})", poiJpegs.Count, modelName);
                parts = BuildPoiParts(poiJpegs);
            }
            else
            {
                var (compressed, _) = ResizeAndCompress(fullFramePng);
                _logger.LogInformation("Frame compressed: {Original} KB → {Compressed} KB",
                    fullFramePng.Length / 1024, compressed.Length / 1024);
                parts = new List<IPart>
                {
                    new InlineData { MimeType = "image/jpeg", Data = Convert.ToBase64String(compressed) },
                    new TextData   { Text = "Identify any wild animals in this image." }
                };
            }

            var response = await model.GenerateContent(parts);
            var raw      = response.Text ?? string.Empty;
            _logger.LogInformation("Gemini raw response: {Raw}", raw);

            return ParseResponse(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : string.Empty;
            _logger.LogError("Gemini API error [{Type}]: {Message}.{Inner}", ex.GetType().Name, ex.Message, inner);
            return new RecognitionResult(false, string.Empty, string.Empty, 0, string.Empty, ex.Message, Array.Empty<SpeciesCandidate>(), SourcePoiIndex: null);
        }
    }

    // ── Content builders ──────────────────────────────────────────────────

    private static List<IPart> BuildPoiParts(IReadOnlyList<byte[]> crops)
    {
        var parts = new List<IPart>();
        for (int i = 0; i < crops.Count; i++)
        {
            parts.Add(new TextData { Text = $"Region {i + 1}:" });
            parts.Add(new InlineData
            {
                MimeType = "image/jpeg",
                Data     = Convert.ToBase64String(crops[i])
            });
        }
        parts.Add(new TextData
        {
            Text = "These are cropped regions from a garden wildlife camera. " +
                   "Identify any wild animals visible across these images. " +
                   "Report the best match found and include \"source_crop_index\" (1-based integer) for which region it appeared in, or detected:false if none are present."
        });
        return parts;
    }

    // ── Image helpers ─────────────────────────────────────────────────────

    private static (byte[] bytes, string mediaType) ResizeAndCompress(byte[] pngBytes)
    {
        const int MaxDimension = 1280;

        using var ms  = new System.IO.MemoryStream(pngBytes);
        var bitmap    = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        BitmapSource source = bitmap;
        if (bitmap.PixelWidth > MaxDimension || bitmap.PixelHeight > MaxDimension)
        {
            var scale = Math.Min((double)MaxDimension / bitmap.PixelWidth,
                                 (double)MaxDimension / bitmap.PixelHeight);
            source    = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new System.IO.MemoryStream();
        encoder.Save(output);
        return (output.ToArray(), "image/jpeg");
    }

    // ── Response parsing ──────────────────────────────────────────────────

    private static RecognitionResult ParseResponse(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```"))
            json = Regex.Replace(json, @"```[a-z]*\r?\n?", string.Empty).Replace("```", string.Empty).Trim();

        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;

        bool detected = root.TryGetProperty("detected", out var det) && det.GetBoolean();

        var candidates = new List<SpeciesCandidate>();
        if (root.TryGetProperty("candidates", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cArr.EnumerateArray())
            {
                candidates.Add(new SpeciesCandidate(
                    CommonName:     c.TryGetProperty("common_name",     out var cn)   ? cn.GetString()   ?? string.Empty : string.Empty,
                    ScientificName: c.TryGetProperty("scientific_name", out var sn)   ? sn.GetString()   ?? string.Empty : string.Empty,
                    Confidence:     c.TryGetProperty("confidence",      out var conf) ? conf.GetDouble() : 0,
                    Description:    c.TryGetProperty("description",     out var desc) ? desc.GetString() ?? string.Empty : string.Empty));
            }
        }

        int? sourceCropIndex = null;
        if (root.TryGetProperty("source_crop_index", out var sci) && sci.ValueKind == JsonValueKind.Number)
            sourceCropIndex = sci.GetInt32();

        var top = candidates.Count > 0 ? candidates[0] : null;

        return new RecognitionResult(
            Detected:       detected,
            CommonName:     top?.CommonName     ?? string.Empty,
            ScientificName: top?.ScientificName ?? string.Empty,
            Confidence:     top?.Confidence     ?? 0,
            Description:    top?.Description    ?? string.Empty,
            RawResponse:    raw,
            Candidates:     candidates,
            SourcePoiIndex: sourceCropIndex);
    }
}
