using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
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
        Assert.Equal("Snapping window right…", ActivityMessage.ForStep(
            new SnapWindowStep("s3", new CurrentWindowTarget(), SnapDirection.Right), null));
        Assert.Equal("Scrolling down…", ActivityMessage.ForBrowserAction(
            new BrowserActivity(InteractionActionKind.Scroll, Direction: "down")));
    }

    [Fact]
    public void BrowserMessagesPreferKnownGoalAndDestination()
    {
        Assert.Equal("Searching for React…", ActivityMessage.ForBrowserAction(
            new BrowserActivity(Query: "React", Destination: "Google", OpeningTab: true)));
        Assert.Equal("Opening GitHub tab…", ActivityMessage.ForBrowserAction(
            new BrowserActivity(Destination: "GitHub", OpeningTab: true)));
        Assert.Equal("Opening React docs…", ActivityMessage.ForBrowserAction(
            new BrowserActivity(InteractionActionKind.Activate,
                TargetRole: "link", TargetName: "React docs")));
        Assert.Equal("Entering text…", ActivityMessage.ForBrowserAction(
            new BrowserActivity(InteractionActionKind.SetText)));
    }

    [Fact]
    public void InternalDetailsMapToGenericMessages()
    {
        Assert.Equal("Which one did you mean?", ActivityMessage.ForClarification("SESSION_MISMATCH: id=42"));
        Assert.Equal("Couldn't complete that action.", ActivityMessage.ForFailure("System.Exception: secret"));
    }
}
