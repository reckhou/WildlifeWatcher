using WildlifeWatcher.Models;

namespace WildlifeWatcher.Tests;

public class AppConfigurationTests
{
    [Fact]
    public void AppConfiguration_Defaults_AreReasonable()
    {
        var config = new AppConfiguration();

        Assert.Equal(30, config.CooldownSeconds);
        Assert.Equal(30, config.FrameExtractionIntervalSeconds);
        Assert.Equal(0.7, config.MinConfidenceThreshold);
        Assert.True(config.EnableLocalPreFilter);
        Assert.Equal(AiProvider.Claude, config.AiProvider);
    }
}