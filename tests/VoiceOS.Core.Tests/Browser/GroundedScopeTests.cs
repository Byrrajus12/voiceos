using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class GroundedScopeTests
{
    private sealed class Picker(ContextualSurface choice = ContextualSurface.Clarify) : IContextualScopeDecisionSource
    {
        public IReadOnlyList<ContextualSurface> Offered = [];
        public int Calls;
        public NamedTabSelection Named = NamedTabSelection.Unavailable("choice_confidence_below_threshold");
        public ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
            ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken cancellationToken = default)
        { Calls++; Offered = offered.ToArray(); return ValueTask.FromResult(choice); }
        public ValueTask<NamedTabSelection> SelectNamedTabAsync(string utterance, IReadOnlyList<BrowserTabInfo> tabs,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Named);
        public ValueTask<string?> SelectInstalledAppAsync(string utterance, IReadOnlyList<AppCandidate> apps,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }
    private static readonly WindowCandidate Chrome = new("w", "chrome", "Chrome", true, 42);
    private static readonly WindowCandidate Explorer = new("w", "explorer", "File Explorer", true, 42);
    private static BrowserTabInfo Tab(int id = 1, bool active = true, bool owned = false)
        => new(id, 1, active, $"https://example.org/{id}", "Results", owned ? BrowserTabProvenance.VoiceOs : BrowserTabProvenance.User, owned ? "s" : null);
    private static CommandRouteDecision Route => new(CommandRoute.Clarify, .3, Reason: RoutingReason.LowConfidence,
        TaskRelation: TaskRelation.Uncertain, MediaRequestKind: MediaRequestKind.None);
    private static readonly DirectOffer Offer = new("Snap File Explorer left", new([new SnapWindowStep("s1", new CurrentWindowTarget(), SnapDirection.Left)]));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContextualPicker_OffersDirect_OnlyWhenSafeProgramExists(bool safe)
    {
        var picker = new Picker(ContextualSurface.DirectCapability);
        var scope = await new ScopeResolver().ResolveAsync("snap it", Route, new(Explorer, [Explorer], true, []), picker,
            directOffer: _ => ValueTask.FromResult(safe ? Offer : null));
        Assert.Equal(safe, picker.Offered.Contains(ContextualSurface.DirectCapability));
        Assert.Equal(safe ? ExecutionScopeKind.DirectCapability : ExecutionScopeKind.Clarify, scope.Kind);
    }

    [Fact]
    public async Task DirectOffer_NotInvokedWhenPickerNotReached()
    {
        var calls = 0;
        var scope = await new ScopeResolver().ResolveAsync("open YouTube", Route with {
            Route = CommandRoute.ComputerUse, DestinationKind = SemanticDestinationKind.KnownService, DestinationName = "YouTube" },
            new(Chrome, [Chrome], true, [Tab()]), new Picker(),
            directOffer: _ => { calls++; return ValueTask.FromResult<DirectOffer?>(Offer); });
        Assert.Equal(0, calls);
        Assert.Equal(ExecutionScopeKind.Browser, scope.Kind);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public async Task RecentOwnedOffered_OnlyUnderNarrowRules(bool active, bool established, bool otherForeground, bool expected)
    {
        var tab = Tab(7, active, true);
        var frame = new RecentTaskFrame(7, "s", tab.Url!, "Find result", InteractionCompletionState.Complete, DateTimeOffset.UtcNow);
        var picker = new Picker(ContextualSurface.RecentOwnedBrowserTab);
        var scope = await new ScopeResolver().ResolveAsync("continue", Route with {
            TaskRelation = TaskRelation.ContinueRecent, TaskRelationEstablished = established,
            ContextDependency = ContextDependency.SelfContained },
            new(otherForeground ? Chrome : Explorer, [Explorer], true, otherForeground ? [tab, Tab(1)] : [tab], frame), picker);
        Assert.Equal(expected, picker.Offered.Contains(ContextualSurface.RecentOwnedBrowserTab));
        if (expected) Assert.Equal(7, scope.Browser?.TabId);
        else Assert.NotEqual(7, scope.Browser?.TabId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PriorWorkRelation_VisiblePage_ConsultsPicker(bool visible)
    {
        var picker = new Picker(ContextualSurface.ActiveBrowserTab);
        var scope = await new ScopeResolver().ResolveAsync("open the third result", Route with {
            Route = CommandRoute.ComputerUse, TaskRelation = TaskRelation.ContinueRecent },
            new(visible ? Chrome : Explorer, [Chrome], true, [Tab()]), picker);
        Assert.Equal(visible ? 1 : 0, picker.Calls);
        Assert.Equal(visible ? ExecutionScopeKind.Browser : ExecutionScopeKind.Clarify, scope.Kind);
        if (visible) Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser?.Kind);
    }

    [Fact]
    public async Task LowConfidenceNamedTabVerdict_FallsThrough_WithoutWeakSwitch()
    {
        var picker = new Picker();
        var scope = await new ScopeResolver().ResolveAsync("open the one from Keychron", Route with {
            Route = CommandRoute.ComputerUse, TabDisposition = TabDisposition.ExistingNamedTab,
            ContextDependency = ContextDependency.RequiresCurrentSurface },
            new(Chrome, [Chrome], true, [Tab(1), Tab(7, false)]), picker);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser?.Kind);
        Assert.Equal(1, scope.Browser?.TabId);
        Assert.False(scope.Browser!.ExplicitSelection);
    }

    [Fact]
    public async Task GenuineBrowserDirectAmbiguity_PickerCanStillClarify()
    {
        var picker = new Picker();
        var scope = await new ScopeResolver().ResolveAsync("do that", Route, new(Chrome, [Chrome], true, [Tab()]), picker,
            directOffer: _ => ValueTask.FromResult<DirectOffer?>(Offer));
        Assert.Contains(ContextualSurface.DirectCapability, picker.Offered);
        Assert.Contains(ContextualSurface.ActiveBrowserTab, picker.Offered);
        Assert.Equal(ExecutionScopeKind.Clarify, scope.Kind);
    }

    [Fact]
    public async Task NoCompanion_NeverOffersBrowserTabSurfaces()
    {
        var picker = new Picker();
        await new ScopeResolver().ResolveAsync("do that", Route, new(Chrome, [Chrome], false, [Tab(7, true, true)]), picker);
        Assert.DoesNotContain(ContextualSurface.ActiveBrowserTab, picker.Offered);
        Assert.DoesNotContain(ContextualSurface.RecentOwnedBrowserTab, picker.Offered);
    }

    [Fact]
    public async Task ClickX_RequiresCurrentSurface_UsesActiveTab_WithoutPicker()
    {
        var picker = new Picker();
        var scope = await new ScopeResolver().ResolveAsync("click Sharp Objects", Route with {
            Route = CommandRoute.ComputerUse, ContextDependency = ContextDependency.RequiresCurrentSurface },
            new(Chrome, [Chrome], true, [Tab()]), picker);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser?.Kind);
        Assert.Equal(0, picker.Calls);
    }

    [Fact]
    public async Task GroundingDisabled_PreservesPriorWorkClarification()
    {
        var picker = new Picker(ContextualSurface.ActiveBrowserTab);
        var scope = await new ScopeResolver().ResolveAsync("continue", Route with { TaskRelation = TaskRelation.ContinueRecent },
            new(Chrome, [Chrome], true, [Tab()]), picker, grounded: false);
        Assert.Equal(ExecutionScopeKind.Clarify, scope.Kind);
        Assert.Equal(0, picker.Calls);
    }
}
