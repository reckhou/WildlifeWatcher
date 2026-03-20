using WildlifeWatcher.Models;
using WildlifeWatcher.Services.Interfaces;

namespace WildlifeWatcher.Services;

/// <summary>
/// Delegates to the correct AI recognition service based on the current <see cref="AiProvider"/> setting.
/// This allows switching providers at runtime without an app restart.
/// </summary>
public class AiRecognitionServiceResolver : IAiRecognitionService
{
    private readonly ClaudeRecognitionService _claude;
    private readonly GeminiRecognitionService _gemini;
    private readonly ISettingsService _settings;

    public AiRecognitionServiceResolver(
        ClaudeRecognitionService claude,
        GeminiRecognitionService gemini,
        ISettingsService settings)
    {
        _claude   = claude;
        _gemini   = gemini;
        _settings = settings;
    }

    public Task<IReadOnlyList<RecognitionResult>> RecognizeAsync(
        byte[] fullFramePng,
        IReadOnlyList<byte[]>? poiJpegs = null,
        CancellationToken ct = default)
    {
        var service = _settings.CurrentSettings.AiProvider == AiProvider.Gemini
            ? (IAiRecognitionService)_gemini
            : _claude;

        return service.RecognizeAsync(fullFramePng, poiJpegs, ct);
    }
}
