using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>A Search step runs on the search surface it meant, never on one that merely looks like a search box.</summary>
public sealed class SearchScopeTests
{
    private static BrowserElement El(string r, string role, string name, bool editable = false, bool search = false, string? value = null)
        => new(r, role, name, true, editable, value, null, new(0, 0, 100, 20, true), "", search);

    private sealed class Transport(params BrowserElement[] elements) : IChromeCompanionTransport
    {
        private BrowserSnapshot Snap(string s, int t) => new(t, s, "r1", "https://example.org/", "Home", "", false, new(1280, 800, 0, 0), elements, false);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default) => ValueTask.FromResult(Snap(a.SessionId, a.TabId));
    }

    private sealed class Gateway : IJevGateway
    {
        public int Calls;
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken c = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "BLOCKED", new Dictionary<string, double> { ["BLOCKED"] = .9, ["CLICK"] = .05 }, .9)
            });
        }
    }

    private static async Task<(InteractionDecision Decision, int ModelCalls)> Decide(SearchScopeIntent intent, params BrowserElement[] elements)
    {
        var step = new PlanStep(PlanStepKind.Search, "Search for Rust GUI framework", "Rust GUI framework", Progress: null, ScopeIntent: intent);
        var plan = new InteractionPlan("x", "x", [step]);
        var goal = new BrowserGoal("x") with { Plan = plan };
        var gateway = new Gateway();
        var decisions = new TypeSafeBrowserDecisionSource(gateway, goal) { Plan = plan };
        var surface = new BrowserSurface(new Transport(elements), goal, decisions, "s");
        var observation = await surface.ObserveAsync();
        var context = new InteractionDecisionContext(new("x", PlanFraming.Frame(plan, goal, null, "t")), observation, [],
            new InteractionBudget(), new(0, 0, 0, 0));
        return (await decisions.DecideAsync(context), gateway.Calls);
    }

    private static readonly BrowserElement CollectionFinder = El("e1", "textbox", "Find a repository…", editable: true, search: true);
    private static readonly BrowserElement GlobalOpener = El("e2", "button", "Open quick search dialog, type / to search");

    [Fact]
    public void ScopeIsDerivedFromTheControlsOwnWording()
    {
        string Scope(string name) => BrowserDomFacts.SearchScopeOf(El("e", "textbox", name, editable: true));
        Assert.Equal(SearchScopes.Collection, Scope("Find a repository…"));
        Assert.Equal(SearchScopes.Global, Scope("Search or jump to…"));
        Assert.Equal(SearchScopes.Global, Scope("Search Amazon"));
        Assert.Equal(SearchScopes.CurrentResource, Scope("Search this repository"));
        Assert.Equal(SearchScopes.InPage, Scope("Find in page"));
        Assert.Equal(SearchScopes.Filter, Scope("Filter repositories"));
        Assert.Equal(SearchScopes.Unknown, Scope("Email address"));
    }

    [Fact]
    public async Task GlobalSearch_OpensTheGlobalSearch_AndNeverTypesIntoTheCollectionFinder()
    {
        var (decision, modelCalls) = await Decide(SearchScopeIntent.Global, CollectionFinder, GlobalOpener);
        Assert.Equal(InteractionActionKind.Activate, decision.Action!.Kind);
        Assert.Equal("e2", decision.Action.TargetId);
        Assert.Equal(0, modelCalls);
    }

    [Fact]
    public async Task CurrentResourceSearch_UsesTheSearchInsideTheOpenResource()
    {
        var (decision, _) = await Decide(SearchScopeIntent.CurrentResource,
            El("e1", "searchbox", "Search GitHub", editable: true), El("e2", "searchbox", "Search this repository", editable: true));
        Assert.Equal(InteractionActionKind.SetText, decision.Action!.Kind);
        Assert.Equal("e2", decision.Action.TargetId);
    }

    [Fact]
    public async Task InPageSearch_DoesNotPickTheGlobalSearch()
    {
        var (decision, _) = await Decide(SearchScopeIntent.InPage,
            El("e1", "searchbox", "Search Wikipedia", editable: true), El("e2", "textbox", "Find in page", editable: true));
        Assert.Equal("e2", decision.Action!.TargetId);
        var (onlyGlobal, modelCalls) = await Decide(SearchScopeIntent.InPage, El("e1", "searchbox", "Search Wikipedia", editable: true));
        Assert.Null(onlyGlobal.Action);   // nothing of that scope: the model is asked, nothing is typed
        Assert.Equal(1, modelCalls);
    }

    [Fact]
    public async Task OnlyMismatchedSurfaces_NeverTakeTheDeterministicShortcut()
    {
        var (decision, modelCalls) = await Decide(SearchScopeIntent.Global, CollectionFinder,
            El("e2", "textbox", "Filter repositories", editable: true, search: true));
        Assert.NotEqual(InteractionActionKind.SetText, decision.Action?.Kind);
        Assert.Equal(1, modelCalls);
    }

    [Fact]
    public void AQueryTypedIntoAnotherSurface_DoesNotCompleteTheSearchStep()
    {
        var step = new OutcomeStep("s2", ProofFamily.Find, new("Search", "Rust GUI framework", null, SearchScopes.Global), WithinPlan: true);
        var submit = new InteractionAction("a2", InteractionActionKind.Activate, "e9");
        var nav = new Effect(EffectKind.Navigated, EffectSource.SnapshotDelta, EffectStrength.Derived, null,
            new Dictionary<string, string> { ["to"] = "https://example.org/results" }) { Id = "n", ActionId = "a2" };
        Effect Typed(string scope) => new(EffectKind.TextSet, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["value"] = "Rust GUI framework", ["matched"] = "true", ["scope"] = scope }) { Id = "t", ActionId = "a1" };
        var evaluator = new PlannedStepEvaluator(new BrowserGoal("x"), new BrowserProofEvaluator(new BrowserGoal("x")));
        var observation = new InteractionObservation(1, "k", "{}", []);
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [Typed(SearchScopes.Collection), nav], submit, null, observation)).Status);
        Assert.Equal(ProofStatus.Proved, evaluator.Evaluate(new(step, [Typed(SearchScopes.Global), nav], submit, null, observation)).Status);
    }
}
