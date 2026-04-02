using WildlifeWatcher.Models;

namespace WildlifeWatcher.Services;

public static class PromptBuilder
{
    private const string JsonFormat =
        "Respond with strict JSON only — no markdown, no code fences. " +
        "Format: {\"detections\":[{\"source_crop_index\":1,\"detected\":true,\"candidates\":[{\"common_name\":\"Red Fox\",\"scientific_name\":\"Vulpes vulpes\",\"confidence\":0.92,\"description\":\"...\"},{\"common_name\":\"Badger\",\"scientific_name\":\"Meles meles\",\"confidence\":0.05,\"description\":\"...\"}]},{\"source_crop_index\":2,\"detected\":true,\"candidates\":[{\"common_name\":\"Robin\",\"scientific_name\":\"Erithacus rubecula\",\"confidence\":0.87,\"description\":\"...\"}]}]} " +
        "Report one entry per crop region that contains an animal. For each detected animal, list up to 3 candidate species in descending confidence order. " +
        "Omit source_crop_index when analysing a single full frame. " +
        "If no animals are visible in any region: {\"detections\":[]}";

    /// <summary>
    /// Builds the full system prompt from user-configured context fields.
    /// </summary>
    public static string Build(AppConfiguration settings)
    {
        var habitat = string.IsNullOrWhiteSpace(settings.AiHabitatDescription)
            ? "a garden"
            : settings.AiHabitatDescription.Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a wildlife identification assistant.");
        sb.AppendLine($"This camera is mounted in {habitat} to observe wild animals and birds.");

        if (!string.IsNullOrWhiteSpace(settings.LocationName))
            sb.AppendLine($"Location: {settings.LocationName.Trim()}.");

        if (!string.IsNullOrWhiteSpace(settings.AiTargetSpeciesHint))
            sb.AppendLine($"Focus on: {settings.AiTargetSpeciesHint.Trim()}.");

        sb.Append(JsonFormat);
        return sb.ToString();
    }

    /// <summary>
    /// Returns only the context lines for UI preview (no JSON format instructions).
    /// </summary>
    public static string BuildPreview(string habitatDescription, string locationName, string speciesHint)
    {
        var habitat = string.IsNullOrWhiteSpace(habitatDescription)
            ? "a garden"
            : habitatDescription.Trim();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are a wildlife identification assistant.");
        sb.AppendLine($"This camera is mounted in {habitat} to observe wild animals and birds.");

        if (!string.IsNullOrWhiteSpace(locationName))
            sb.AppendLine($"Location: {locationName.Trim()}.");

        if (!string.IsNullOrWhiteSpace(speciesHint))
            sb.Append($"Focus on: {speciesHint.Trim()}.");

        return sb.ToString().TrimEnd();
    }
}
