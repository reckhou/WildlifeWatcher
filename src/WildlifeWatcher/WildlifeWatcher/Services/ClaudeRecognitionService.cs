using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using WildlifeWatcher.Services.Interfaces;
using ImageSource = Anthropic.SDK.Messaging.ImageSource;
using Message = Anthropic.SDK.Messaging.Message;

namespace WildlifeWatcher.Services;

public class ClaudeRecognitionService : IAiRecognitionService
{
    private const string SystemPrompt =
        "You are a garden wildlife identification assistant. " +
        "This camera is mounted in a domestic garden, so the most commonly seen animals are domestic cats, dogs, and garden birds (robins, pigeons, sparrows, starlings, blackbirds, etc.). " +
        "Give strong preference to these species when the image is ambiguous — only identify as a rarer wild animal if the visual evidence clearly rules out cats, dogs, and birds. " +
        "Respond with strict JSON only — no markdown, no code fences. " +
        "Format: {\"detected\":true,\"candidates\":[{\"common_name\":\"Domestic Cat\",\"scientific_name\":\"Felis catus\",\"confidence\":0.92,\"description\":\"...\"},{\"common_name\":\"Red Fox\",\"scientific_name\":\"Vulpes vulpes\",\"confidence\":0.05,\"description\":\"...\"}]} " +
        "List up to 3 most likely animals in descending confidence order. " +
        "If no animal is visible: {\"detected\":false,\"candidates\":[]}";

    private readonly ISettingsService _settings;
    private readonly ICredentialService _credentials;
    private readonly ILogger<ClaudeRecognitionService> _logger;

    public ClaudeRecognitionService(
        ISettingsService settings,
        ICredentialService credentials,
        ILogger<ClaudeRecognitionService> logger)
    {
        _settings = settings;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<RecognitionResult> RecognizeAsync(
        byte[] fullFramePng,
        IReadOnlyList<byte[]>? poiJpegs = null,
        CancellationToken ct = default)
    {
        var apiKey = _credentials.LoadCredentials()?.AnthropicApiKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No Anthropic API key configured; skipping AI recognition");
            return new RecognitionResult(false, string.Empty, string.Empty, 0, string.Empty, string.Empty, Array.Empty<SpeciesCandidate>());
        }

        var model = _settings.CurrentSettings.ClaudeModel;

        // Build image content — prefer POI crops over full frame
        List<ContentBase> content;
        if (poiJpegs is { Count: > 0 })
        {
            _logger.LogInformation("Sending {Count} POI crop(s) to AI ({Model})", poiJpegs.Count, model);
            content = BuildPoiContent(poiJpegs);
        }
        else
        {
            var (compressed, mediaType) = ResizeAndCompress(fullFramePng);
            _logger.LogInformation("Frame compressed: {Original} KB → {Compressed} KB",
                fullFramePng.Length / 1024, compressed.Length / 1024);
            content = BuildSingleImageContent(compressed, mediaType);
        }

        try
        {
            var client = new AnthropicClient(apiKey);

            var messages = new List<Message>
            {
                new()
                {
                    Role    = RoleType.User,
                    Content = content
                }
            };

            var parameters = new MessageParameters
            {
                Model     = model,
                MaxTokens = 1024,
                System    = new List<SystemMessage> { new(SystemPrompt) },
                Messages  = messages
            };

            var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);
            var raw      = response.Message.ToString() ?? string.Empty;
            _logger.LogInformation("Claude raw response: {Raw}", raw);

            return ParseResponse(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : string.Empty;
            _logger.LogError("Claude API error [{Type}]: {Message}.{Inner}", ex.GetType().Name, ex.Message, inner);
            return new RecognitionResult(false, string.Empty, string.Empty, 0, string.Empty, ex.Message, Array.Empty<SpeciesCandidate>());
        }
    }

    // ── Content builders ──────────────────────────────────────────────────

    private static List<ContentBase> BuildSingleImageContent(byte[] imageBytes, string mediaType)
    {
        return new List<ContentBase>
        {
            new ImageContent
            {
                Source = new ImageSource { MediaType = mediaType, Data = Convert.ToBase64String(imageBytes) }
            },
            new TextContent { Text = "Identify any wild animals in this image." }
        };
    }

    private static List<ContentBase> BuildPoiContent(IReadOnlyList<byte[]> crops)
    {
        var content = new List<ContentBase>();
        for (int i = 0; i < crops.Count; i++)
        {
            content.Add(new TextContent { Text = $"Region {i + 1}:" });
            content.Add(new ImageContent
            {
                Source = new ImageSource
                {
                    MediaType = "image/jpeg",
                    Data      = Convert.ToBase64String(crops[i])
                }
            });
        }
        content.Add(new TextContent
        {
            Text = "These are cropped regions from a garden wildlife camera. " +
                   "Identify any wild animals visible across these images. " +
                   "Report the best match found, or detected:false if none are present."
        });
        return content;
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

        var top = candidates.Count > 0 ? candidates[0] : null;

        return new RecognitionResult(
            Detected:       detected,
            CommonName:     top?.CommonName     ?? string.Empty,
            ScientificName: top?.ScientificName ?? string.Empty,
            Confidence:     top?.Confidence     ?? 0,
            Description:    top?.Description    ?? string.Empty,
            RawResponse:    raw,
            Candidates:     candidates);
    }
}
