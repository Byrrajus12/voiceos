using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Tests.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ReferentScopeTests
{
    private sealed class Chooser(ReferentChoice choice) : IContextualScopeDecisionSource
    {
        public int ReferentCalls;
        public int SurfaceCalls;
        public ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
            ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken cancellationToken = default)
        { SurfaceCalls++; return ValueTask.FromResult(ContextualSurface.Clarify); }
        public ValueTask<ReferentChoice> SelectReferentAsync(string utterance, IReadOnlyList<ReferentCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            ReferentCalls++;
            return ValueTask.FromResult(choice.Kind == ReferentChoiceKind.Selected && choice.CandidateId == "first"
                ? choice with { CandidateId = candidates[0].Id } : choice);
        }
    }

    private static readonly WindowCandidate Chrome = new("w", "chrome", "Chrome", true, 42);
    private static readonly WindowCandidate Notepad = new("w", "notepad", "Notepad", true, 43);
    private static Referent Seq(Referent r, long seq) => r with { Seq = seq };

    private static CommandRouteDecision Route(TaskRelation relation = TaskRelation.ContinueRecent,
        ContextDependency dependency = ContextDependency.Uncertain,
        SemanticEndState end = SemanticEndState.SurfaceReady, GoalShape shape = GoalShape.SurfaceOnly)
        => new(CommandRoute.ComputerUse, .9, TaskRelation: relation, ContextDependency: dependency,
            EndState: end, GoalShape: shape, MediaRequestKind: MediaRequestKind.None) { TaskRelationEstablished = true };

    private static ExecutionContextSnapshot Context(WindowCandidate foreground, IReadOnlyList<BrowserTabInfo> tabs, params Referent[] referents)
        => new(foreground, [foreground], true, tabs) { Referents = referents };

    private static Referent DunePage(int tab, string title, long seq)
        => Seq(ReferentStoreTests.Page(tab, $"https://example.org/{tab}", title), seq);

    private static BrowserTabInfo Tab(int id, bool active, string title = "T")
        => new(id, 1, active, $"https://example.org/{id}", title, BrowserTabProvenance.VoiceOs, "s");

    [Fact]
    public async Task EarlierPage_BringsItsLiveTabBack_WithoutTheSurfacePicker()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first", ReferenceRelation.Same));
        var scope = await new ScopeResolver().ResolveAsync("bring that page back", Route(),
            Context(Chrome, [Tab(1, false, "Docs"), Tab(2, true, "Other")], DunePage(1, "Docs", 1)), chooser);
        Assert.Equal(ExecutionScopeKind.Browser, scope.Kind);
        Assert.Equal(BrowserScopeKind.ExistingNamedTab, scope.Browser!.Kind);
        Assert.Equal(1, scope.Browser.TabId);
        Assert.True(scope.Browser.ReferentResolved);
        Assert.True(scope.Browser.IsSurfaceOnly);
        Assert.Equal("Docs", scope.Browser.ExpectedTitle);
        Assert.Equal(0, chooser.SurfaceCalls);
    }

    [Fact]
    public async Task ResolvedReferentInView_UsesTheActiveTab()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var scope = await new ScopeResolver().ResolveAsync("open its pilot", Route(shape: GoalShape.ActionOnSurface, end: SemanticEndState.ResourceOpened),
            Context(Chrome, [Tab(1, true)], DunePage(1, "Docs", 1)), chooser);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser!.Kind);
        Assert.True(scope.Browser.ReferentResolved);
        Assert.False(scope.Browser.FocusOnly);
        Assert.Equal(0, chooser.ReferentCalls); // unique current page: deterministic
    }

    [Fact]
    public async Task ClosedItemPage_ReopensItsAddressInANewTab()
    {
        var item = Seq(ReferentStoreTests.Item("https://imdb.test/dune21", "Dune (2021)"), 1);
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first", ReferenceRelation.Same));
        var scope = await new ScopeResolver().ResolveAsync("open that again", Route(),
            Context(Chrome, [Tab(5, true)], item), chooser);
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser!.Kind);
        Assert.Equal("https://imdb.test/dune21", scope.Browser.Destination!.AbsoluteUri);
        Assert.True(scope.Browser.ReferentResolved);
        Assert.True(scope.Browser.IsSurfaceOnly);
    }

    [Fact]
    public async Task AmbiguousReference_Clarifies_InsteadOfActing()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Ambiguous));
        var scope = await new ScopeResolver().ResolveAsync("open that", Route(),
            Context(Notepad, [Tab(1, false), Tab(2, false)], DunePage(1, "A", 1), DunePage(2, "B", 2)), chooser);
        Assert.Equal(ExecutionScopeKind.Clarify, scope.Kind);
    }

    [Fact]
    public async Task NoneFromPicker_FallsBackToLegacyScope()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.None));
        var scope = await new ScopeResolver().ResolveAsync("continue that", Route(),
            Context(Notepad, [Tab(1, false)], DunePage(1, "A", 1)), chooser);
        // Legacy: prior-work dependence without a safe current surface clarifies.
        Assert.Equal(ExecutionScopeKind.Unresolved, scope.Kind);
        Assert.Equal(1, chooser.ReferentCalls);
    }

    [Fact]
    public async Task CurrentSurfaceReference_KeepsPrecedenceOverStoredReferents()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var scope = await new ScopeResolver().ResolveAsync("click that button",
            Route(dependency: ContextDependency.RequiresCurrentSurface, shape: GoalShape.ActionOnSurface, end: SemanticEndState.StateChanged),
            Context(Chrome, [Tab(1, false, "Old"), Tab(2, true, "Now")], DunePage(1, "Old", 1)), chooser);
        Assert.Equal(0, chooser.ReferentCalls);
        Assert.Equal(2, scope.Browser?.TabId);
        Assert.False(scope.Browser!.ReferentResolved);
    }

    [Fact]
    public async Task NoReferents_LeavesLegacyBehaviorUntouched()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var scope = await new ScopeResolver().ResolveAsync("continue", Route(),
            Context(Notepad, [Tab(1, false)]), chooser);
        Assert.Equal(0, chooser.ReferentCalls);
        Assert.Equal(ExecutionScopeKind.Unresolved, scope.Kind);
    }

    [Fact]
    public async Task NewTaskRelation_IgnoresReferents()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        await new ScopeResolver().ResolveAsync("open youtube", Route(relation: TaskRelation.NewTask, dependency: ContextDependency.SelfContained),
            Context(Chrome, [Tab(1, true)], DunePage(1, "A", 1)), chooser);
        Assert.Equal(0, chooser.ReferentCalls);
    }

    [Fact]
    public async Task ExplicitTabDisposition_IsNotOverriddenByReferents()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        await new ScopeResolver().ResolveAsync("open it in a new tab", Route() with { TabDisposition = TabDisposition.NewTab },
            Context(Chrome, [Tab(1, true)], DunePage(1, "A", 1)), chooser);
        Assert.Equal(0, chooser.ReferentCalls);
    }

    [Fact]
    public async Task ReferentWhoseTabIsGone_NeverActs()
    {
        // The store validated nothing for tab 9, so nothing offers it; a stale tab cannot be selected.
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var scope = await new ScopeResolver().ResolveAsync("bring that back", Route(),
            Context(Notepad, [Tab(1, false)], DunePage(9, "Closed", 1)), chooser);
        Assert.NotEqual(9, scope.Browser?.TabId);
        Assert.Equal(ExecutionScopeKind.Unresolved, scope.Kind);
    }

    [Fact]
    public async Task RouterSaysEarlier_EvenWhenNewTaskAndVisibleSurface_TheReferentsSettleIt()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var route = Route(relation: TaskRelation.NewTask, dependency: ContextDependency.RequiresCurrentSurface) with { ReferencesEarlier = true };
        var scope = await new ScopeResolver().ResolveAsync("now the other one", route,
            Context(Chrome, [Tab(1, false, "Stove"), Tab(2, true, "Bag")], DunePage(1, "Stove", 5), DunePage(2, "Bag", 2)), chooser);
        Assert.Equal(1, chooser.ReferentCalls);
        Assert.Equal(1, scope.Browser!.TabId);
        Assert.True(scope.Browser.ReferentResolved);
    }

    [Fact]
    public async Task RouterSaysNotEarlier_WithNewTask_NeverConsultsReferents()
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var route = Route(relation: TaskRelation.NewTask, dependency: ContextDependency.RequiresCurrentSurface);
        await new ScopeResolver().ResolveAsync("show me the tent", route,
            Context(Chrome, [Tab(1, false), Tab(2, true)], DunePage(1, "Stove", 1)), chooser);
        Assert.Equal(0, chooser.ReferentCalls);
    }

    [Theory]
    [InlineData(GoalShape.Uncertain, true)]
    [InlineData(GoalShape.SurfaceOnly, true)]
    [InlineData(GoalShape.ActionOnSurface, false)]
    public async Task ResolvedReferent_IsFocusOnly_UnlessTheRouterSawAnActionOnIt(GoalShape shape, bool focusOnly)
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var scope = await new ScopeResolver().ResolveAsync("now the other one",
            Route(shape: shape, end: SemanticEndState.Unspecified) with { ReferencesEarlier = true },
            Context(Chrome, [Tab(1, false, "Stove"), Tab(2, true, "Bag")], DunePage(1, "Stove", 5), DunePage(2, "Bag", 2)), chooser);
        Assert.Equal(focusOnly, scope.Browser!.IsSurfaceOnly);
        Assert.Equal(1, scope.Browser.TabId);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]   // not actionable
    [InlineData(true, false, false, false)]   // router did not say earlier
    [InlineData(true, true, true, false)]     // a direct/native/text candidate keeps its route
    public async Task ThinClarifyRoute_UsesReferents_OnlyWhenEarlierAndNotNonBrowser(bool actionable, bool earlier, bool nonBrowser, bool consulted)
    {
        var chooser = new Chooser(new(ReferentChoiceKind.Selected, "first"));
        var route = new CommandRouteDecision(CommandRoute.Clarify, .4, Reason: RoutingReason.AmbiguousIntent,
            MediaRequestKind: MediaRequestKind.None) with
        {
            IntentActionable = actionable, ReferencesEarlier = earlier, CoarseNonBrowserCandidate = nonBrowser,
            TaskRelationEstablished = true, TaskRelation = TaskRelation.NewTask
        };
        var scope = await new ScopeResolver().ResolveAsync("now the other one", route,
            Context(Chrome, [Tab(1, false, "Stove"), Tab(2, true, "Bag")], DunePage(1, "Stove", 5), DunePage(2, "Bag", 2)), chooser);
        Assert.Equal(consulted, chooser.ReferentCalls > 0);
        if (consulted) Assert.Equal(1, scope.Browser!.TabId);
    }
}
