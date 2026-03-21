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
        "This camera is mounted in a domestic garden, so the most commonly seen animals are domestic cats, and garden birds. " +
        "Give slightly preference to these species when the image is ambiguous — only identify as a rarer wild animal if the visual evidence clearly rules out cats and birds. " +
        "Respond with strict JSON only — no markdown, no code fences. " +
        "Format: {\"detections\":[{\"source_crop_index\":1,\"detected\":true,\"candidates\":[{\"common_name\":\"Domestic Cat\",\"scientific_name\":\"Felis catus\",\"confidence\":0.92,\"description\":\"...\"},{\"common_name\":\"Red Fox\",\"scientific_name\":\"Vulpes vulpes\",\"confidence\":0.05,\"description\":\"...\"}]},{\"source_crop_index\":2,\"detected\":true,\"candidates\":[{\"common_name\":\"Robin\",\"scientific_name\":\"Erithacus rubecula\",\"confidence\":0.87,\"description\":\"...\"}]}]} " +
        "Report one entry per crop region that contains an animal. For each detected animal, list up to 3 candidate species in descending confidence order. " +
        "Omit source_crop_index when analysing a single full frame. " +
        "If no animals are visible in any region: {\"detections\":[]}";

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

    public async Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(
        byte[] fullFramePng,
        IReadOnlyList<byte[]>? poiJpegs = null,
        CancellationToken ct = default)
    {
        var apiKey = _credentials.LoadCredentials()?.GeminiApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Gemini API key configured; skipping AI recognition");
            return Array.Empty<RecognitionResult>();
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
            return Array.Empty<RecognitionResult>();
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
                   "Identify any wild animals visible in each region independently. " +
                   "Report one detection entry per region that contains an animal."
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
            source.Freeze();
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new System.IO.MemoryStream();
        encoder.Save(output);
        return (output.ToArray(), "image/jpeg");
    }

    // ── Response parsing ──────────────────────────────────────────────────

    private static IReadOnlyList<RecognitionResult> ParseResponse(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```"))
            json = Regex.Replace(json, @"```[a-z]*\r?\n?", string.Empty).Replace("```", string.Empty).Trim();

        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;

        var results = new List<RecognitionResult>();
        if (!root.TryGetProperty("detections", out var detectionsArr) || detectionsArr.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var det in detectionsArr.EnumerateArray())
        {
            bool detected = det.TryGetProperty("detected", out var detProp) && detProp.GetBoolean();

            var candidates = new List<SpeciesCandidate>();
            if (det.TryGetProperty("candidates", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
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
            if (det.TryGetProperty("source_crop_index", out var sci) && sci.ValueKind == JsonValueKind.Number)
                sourceCropIndex = sci.GetInt32();

            var top = candidates.Count > 0 ? candidates[0] : null;

            results.Add(new RecognitionResult(
                Detected:       detected,
                CommonName:     top?.CommonName     ?? string.Empty,
                ScientificName: top?.ScientificName ?? string.Empty,
                Confidence:     top?.Confidence     ?? 0,
                Description:    top?.Description    ?? string.Empty,
                RawResponse:    raw,
                Candidates:     candidates,
                SourcePoiIndex: sourceCropIndex));
        }

        return results;
    }
}
