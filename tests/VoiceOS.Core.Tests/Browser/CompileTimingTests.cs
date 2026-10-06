using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class CompileTimingTests
{
    [Fact]
    public void ProviderUsage_IsReadIntoTheTimingLine_WithoutAnySecret()
    {
        using var body = System.Text.Json.JsonDocument.Parse("""
            {"provider":"Acme","choices":[{"finish_reason":"stop","message":{"content":"{}"}}],
             "usage":{"prompt_tokens":812,"completion_tokens":396,"completion_tokens_details":{"reasoning_tokens":140}}}
            """);
        var usage = OpenRouterBrowserStepCompiler.CompileUsage.From(body.RootElement);
        var line = new OpenRouterBrowserStepCompiler.CompileTiming(3, 4100, 12, 2, 4150, 0, usage).Format();
        Assert.Contains("provider=4112ms", line);
        Assert.Contains("completion_tokens=396 reasoning_tokens=140 finish=stop", line);
        Assert.Contains("prompt_tokens=812", line);
    }
}
