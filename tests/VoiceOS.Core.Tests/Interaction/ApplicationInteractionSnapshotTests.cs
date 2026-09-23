using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Interaction;

public sealed class ApplicationInteractionSnapshotTests
{
    [Fact]
    public void ProductPhases_AreSurfaceNeutralAndComplete()
    {
        Assert.Equal([
            "Listening", "Transcribing", "Routing", "Observing", "Deciding",
            "Executing", "NeedsChoice", "Succeeded", "Failed", "Idle"
        ], Enum.GetNames<ApplicationInteractionPhase>());
    }
}
