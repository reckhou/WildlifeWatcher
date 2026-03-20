namespace WildlifeWatcher.Models;

public class Credentials
{
    public string RtspUsername { get; set; } = string.Empty;
    public string RtspPassword { get; set; } = string.Empty;
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string GeminiApiKey { get; set; } = string.Empty;
}
