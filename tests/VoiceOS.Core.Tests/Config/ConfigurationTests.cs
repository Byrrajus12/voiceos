using Microsoft.Extensions.Configuration;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Config;
using Xunit;

namespace VoiceOS.Core.Tests.Config;

public class ConfigurationTests
{
    private static VoiceOSConfig BindConfig(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();
        return config.GetSection("VoiceOS").Get<VoiceOSConfig>() ?? new VoiceOSConfig();
    }

    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        var config = new VoiceOSConfig();

        Assert.Equal("RControlKey", config.ActivationKey);
        Assert.Equal(ActivationMode.HoldOrToggle, config.ActivationMode);
        Assert.Equal(300, config.HoldThresholdMs);
        Assert.Equal(50, config.GracePeriodMs);
        Assert.True(config.DebugOutputEnabled);
    }

    [Fact]
    public void TimeSpanProperties_DeriveFromMillisecondFields()
    {
        var config = new VoiceOSConfig { HoldThresholdMs = 400, GracePeriodMs = 200 };

        Assert.Equal(TimeSpan.FromMilliseconds(400), config.HoldThreshold);
        Assert.Equal(TimeSpan.FromMilliseconds(200), config.GracePeriod);
    }

    [Fact]
    public void JsonBinding_ParsesActivationMode()
    {
        var json = """
            {
              "VoiceOS": {
                "ActivationMode": "PushToTalk"
              }
            }
            """;

        var config = BindConfig(json);
        Assert.Equal(ActivationMode.PushToTalk, config.ActivationMode);
    }

    [Fact]
    public void JsonBinding_ParsesAllFields()
    {
        var json = """
            {
              "VoiceOS": {
                "ActivationKey": "LControlKey",
                "ActivationMode": "Toggle",
                "HoldThresholdMs": 500,
                "GracePeriodMs": 100,
                "DebugOutputEnabled": false
              }
            }
            """;

        var config = BindConfig(json);

        Assert.Equal("LControlKey", config.ActivationKey);
        Assert.Equal(ActivationMode.Toggle, config.ActivationMode);
        Assert.Equal(500, config.HoldThresholdMs);
        Assert.Equal(100, config.GracePeriodMs);
        Assert.False(config.DebugOutputEnabled);
    }

    [Fact]
    public void JsonBinding_MissingSection_ReturnsDefaults()
    {
        var config = BindConfig("{}");

        Assert.Equal(ActivationMode.HoldOrToggle, config.ActivationMode);
        Assert.Equal(300, config.HoldThresholdMs);
    }
}
