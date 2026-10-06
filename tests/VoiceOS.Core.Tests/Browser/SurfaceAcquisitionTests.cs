using VoiceOS.Core.Browser;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class SurfaceAcquisitionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var kind in Enum.GetValues<BrowserScopeKind>())
        foreach (var destination in new[] { false, true })
        foreach (var blank in new[] { false, true })
        foreach (var explicitSelection in new[] { false, true })
        foreach (var focus in new[] { false, true })
            yield return [kind, destination, blank, explicitSelection, focus];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AcquiresSurface_TruthTable(BrowserScopeKind kind, bool destination, bool blank, bool explicitSelection, bool focus)
    {
        var scope = new BrowserExecutionScope(kind, Destination: destination ? new Uri("https://example.org/") : null,
            ExplicitSelection: explicitSelection, FocusOnly: focus, EndState: SemanticEndState.SurfaceReady,
            GoalShape: GoalShape.SurfaceOnly) { BlankTabRequested = blank };
        var expected = kind switch {
            BrowserScopeKind.NewTaskTab => destination || blank,
            BrowserScopeKind.ActiveTab => explicitSelection && focus,
            _ => focus };
        Assert.Equal(expected, scope.AcquiresSurface);
        Assert.Equal(expected, scope.IsSurfaceOnly);
        Assert.False((scope with { DestinationPending = true }).IsSurfaceOnly);
        Assert.False((scope with { TabClaimRefuted = true }).IsSurfaceOnly);
        Assert.False((scope with { GoalShape = GoalShape.ActionOnSurface }).IsSurfaceOnly);
    }

    [Fact]
    public void KnownServiceHint_AcquiresDestination_WithoutBlankRequest()
        => Assert.True(new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, NamedServiceHint: "YouTube").AcquiresSurface);

    [Fact]
    public void ClickSharpObjects_NoPage_NewTaskTabWithoutDestination_IsNotSurfaceOnly()
        => Assert.False(new BrowserExecutionScope(BrowserScopeKind.NewTaskTab,
            EndState: SemanticEndState.SurfaceReady, GoalShape: GoalShape.SurfaceOnly).IsSurfaceOnly);
}
