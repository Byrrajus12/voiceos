using VoiceOS.Core.Activation;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class ActivityMessageTests
{
    [Fact]
    public void UsesTypedStepsWithoutTranscriptInference()
    {
        Assert.Equal("Moving window…", ActivityMessage.ForStep(
            new MoveWindowStep("s1", new CurrentWindowTarget(), new VoiceOS.Core.Monitors.CurrentMonitor()), null));
        Assert.Equal("Playing…", ActivityMessage.ForStep(new MediaControlStep("s2", MediaOperation.Play), null));
        Assert.Equal("Scrolling…", ActivityMessage.ForBrowserAction(InteractionActionKind.Scroll));
    }

    [Fact]
    public void InternalDetailsMapToGenericMessages()
    {
        Assert.Equal("Could you clarify that request?", ActivityMessage.ForClarification("SESSION_MISMATCH: id=42"));
        Assert.Equal("Couldn't complete that action.", ActivityMessage.ForFailure("System.Exception: secret"));
    }
}
