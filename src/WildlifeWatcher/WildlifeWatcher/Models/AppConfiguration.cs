namespace WildlifeWatcher.Models;

public enum AiProvider { Claude, Gemini, LocalOnly }

public class AppConfiguration
{
    public string RtspUrl { get; set; } = string.Empty;
    public int CooldownSeconds { get; set; } = 30;
    public string CapturesDirectory { get; set; } = "captures";
    public string ClaudeModel { get; set; } = "claude-haiku-4-5-20251001";
    public int FrameExtractionIntervalSeconds { get; set; } = 30;
    public double MinConfidenceThreshold { get; set; } = 0.7;
    public AiProvider AiProvider { get; set; } = AiProvider.Claude;
    public bool EnableLocalPreFilter { get; set; } = true;
    public string LocalModelPath { get; set; } = string.Empty;
}
