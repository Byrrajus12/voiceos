using VoiceOS.Core.Config;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Config;

public sealed class BrowserProofModeConfigTests
{
    [Fact]
    public void ProofIsOnByDefault() => Assert.Equal(ProofMode.On, new VoiceOSConfig().BrowserProofMode);

    [Theory]
    [InlineData("Off", ProofMode.Off)]
    [InlineData("shadow", ProofMode.Shadow)]
    [InlineData("On", ProofMode.On)]
    [InlineData("garbage", ProofMode.On)]
    public void EnvironmentOverridesTheMode_AndUnknownValuesKeepTheDefault(string value, ProofMode expected)
    {
        var previous = Environment.GetEnvironmentVariable("VOICEOS_BROWSER_PROOF");
        try
        {
            Environment.SetEnvironmentVariable("VOICEOS_BROWSER_PROOF", value);
            var config = new VoiceOSConfig();
            config.ApplyEnvironmentOverrides();
            Assert.Equal(expected, config.BrowserProofMode);
        }
        finally { Environment.SetEnvironmentVariable("VOICEOS_BROWSER_PROOF", previous); }
    }
}
