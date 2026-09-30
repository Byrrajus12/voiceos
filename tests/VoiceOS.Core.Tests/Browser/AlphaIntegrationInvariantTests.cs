using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>The product contract of the integrated alpha runtime, as a handful of invariants.</summary>
public sealed class AlphaIntegrationInvariantTests
{
    // ── destination grounding ────────────────────────────────────────────────

    private sealed class Gateway(Func<IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(questions));
        }
    }

    private static JevAnswer Pick(string value, double p = .95)
        => new("choice", value, new Dictionary<string, double> { [value] = p }, p);

    private static async Task<CommandRouteDecision> Route(string utterance, string destination, params (string, string)[] extra)
    {
        var gateway = new Gateway(_ =>
        {
            var answers = new Dictionary<string, JevAnswer>
            {
                ["route"] = Pick("COMPUTER_USE"), ["intent_completeness"] = Pick("Actionable"),
                ["media_request_kind"] = Pick("None"), ["destination"] = Pick(destination),
                ["goal_shape"] = Pick("ActionOnSurface"), ["end_state"] = Pick("ResultsVisible"),
                ["context_dependency"] = Pick("SelfContained"), ["task_relation"] = Pick("NewTask")
            };
            foreach (var (key, value) in extra) answers[key] = Pick(value);
            return answers;
        });
        return await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
    }

    [Fact]
    public async Task GenericWebSearch_DoesNotInventGitHub()
    {
        // The classifier thought GitHub sensible; the user never said it.
        var route = await Route("Search for Rust GUI framework", "GitHub");
        Assert.Equal(SemanticDestinationKind.None, route.DestinationKind);
        Assert.Null(route.DestinationName);
        Assert.Equal("GitHub", route.SuggestedDestination);

        // The compiler's own suggestion is dropped the same way, so the task starts on the generic web search.
        var compiled = OpenRouterBrowserStepCompiler.Parse("""
            {"finalGoal":"Search Rust GUI framework","endState":"ResultsVisible","resourceType":null,"preferredService":"GitHub",
             "preferredServiceUrl":"https://github.com/","correctedTerms":[],
             "steps":[{"kind":"Reach","description":"Go to GitHub","query":null,"target":"GitHub","progress":null,"searchScope":null},
                      {"kind":"Search","description":"Search","query":"Rust GUI framework","target":null,"progress":null,"searchScope":"Global"}]}
            """, "Search for Rust GUI framework")!;
        var grounded = DestinationGrounding.Ground(compiled, "Search for Rust GUI framework", destinationKnown: false);
        Assert.Null(grounded.Normalization!.PreferredService);
        Assert.DoesNotContain(grounded.Plan.Steps, s => s.Kind == PlanStepKind.Reach);
        Assert.Equal("https://www.google.com/",
            BrowserGoal.BootstrapUrl(new BrowserGoal("Search for Rust GUI framework") with { Normalization = grounded.Normalization }));
    }

    [Fact]
    public async Task ExplicitlyNamedService_IsAuthoritative()
    {
        var route = await Route("Search GitHub for Rust GUI framework", "GitHub");
        Assert.Equal(SemanticDestinationKind.KnownService, route.DestinationKind);
        Assert.Equal("GitHub", route.DestinationName);
        Assert.True(DestinationGrounding.Names("open git hub", "GitHub"));
    }

    // ── surface selection ────────────────────────────────────────────────────

    private static ExecutionContextSnapshot Chrome(params BrowserTabInfo[] tabs)
    {
        var window = new WindowCandidate("w1", "chrome", "Chrome", true, 1);
        return new(window, [window], true, tabs);
    }

    private static BrowserTabInfo Tab(int id, string url, bool active, BrowserTabProvenance provenance = BrowserTabProvenance.User)
        => new(id, 1, active, url, "Tab", provenance, provenance == BrowserTabProvenance.VoiceOs ? $"s{id}" : null, provenance == BrowserTabProvenance.VoiceOs ? id : null);

    [Fact]
    public async Task ExplicitCurrentSurface_BeatsFuzzyNewTaskAndSuggestedDestination()
    {
        // "open the first result on the feed here": relation says NewTask, and a suggested service was not named.
        var route = await Route("open the first result on the feed here", "GitHub",
            ("context_dependency", "RequiresCurrentSurface"), ("task_relation", "NewTask"));
        Assert.Equal(TaskRelation.NewTask, route.TaskRelation);
        var scope = await new ScopeResolver().ResolveAsync("open the first result on the feed here", route,
            Chrome(Tab(3, "https://example.org/feed", true)));
        Assert.Equal(ExecutionScopeKind.Browser, scope.Kind);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser!.Kind);
        Assert.Equal(3, scope.Browser.TabId);
    }

    private sealed class Contextual : IContextualScopeDecisionSource
    {
        public int ReferentCalls { get; private set; }
        public ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
            ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ContextualSurface.NewBrowserTaskTab);
        public ValueTask<ReferentChoice> SelectReferentAsync(string utterance, IReadOnlyList<ReferentCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            ReferentCalls++;
            return ValueTask.FromResult(new ReferentChoice(ReferentChoiceKind.None));
        }
    }

    [Fact]
    public async Task FreshSelfContainedTask_IgnoresUnrelatedReferents()
    {
        var route = await Route("Look up Detroit weather", "None");
        var page = new Referent(ReferentKind.Page, "Old page", ReferentProvenance.ObservedActivePage, DateTimeOffset.UtcNow, 3, "s3", "https://example.org/old", OwnedByVoiceOs: true);
        var context = Chrome(Tab(3, "https://example.org/old", true)) with { Referents = [page] };
        var contextual = new Contextual();
        var scope = await new ScopeResolver().ResolveAsync("Look up Detroit weather", route, context, contextual);
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser!.Kind);
        Assert.Equal(0, contextual.ReferentCalls);
    }

    // ── history: exactly one traversal, no model ─────────────────────────────

    private sealed class HistoryTransport : IChromeCompanionTransport
    {
        private readonly List<string> _pages = ["https://a.example/", "https://b.example/", "https://c.example/"];
        private int _at = 2;
        private int _n;
        public List<string> Acts { get; } = [];
        public int Closes { get; private set; }
        public IReadOnlyList<string> Pages => _pages;
        public bool IsConnected => true;

        private BrowserSnapshot Snap(string session, int tab) => new(tab, session, $"r{++_n}", _pages[_at], "Page", "text", false,
            new(1280, 800, 0, 0), [new("e1", "link", "Go", true, false, null, "https://x.example/", new(0, 0, 10, 10, true), "")]);

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string url, CancellationToken ct = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int tab, CancellationToken ct = default) => ValueTask.FromResult(Snap(s, tab));
        public ValueTask SelectTabAsync(string s, int tab, string url, bool requireActive, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask CloseTaskTabAsync(string s, int tab, CancellationToken ct = default) { Closes++; return ValueTask.CompletedTask; }
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken ct = default)
        {
            Acts.Add(a.Action);
            if (a.Action == "BACK" && _at > 0) _at--;
            else if (a.Action == "FORWARD" && _at < _pages.Count - 1) _at++;
            else if (a.Action is "BACK" or "FORWARD") throw new ChromeCompanionException("NO_HISTORY", "none");
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private static BrowserInteractionService HistoryService(HistoryTransport transport, Gateway? gateway = null)
        => new(transport, gateway ?? new Gateway(_ => throw new InvalidOperationException("no model call expected")));

    private static BrowserExecutionScope Adopted(PageOperation operation, string url = "https://c.example/") => new(BrowserScopeKind.ActiveTab, 1, url)
    {
        PageOperation = operation
    };

    [Fact]
    public async Task GoBack_ExecutesExactlyOneBack_ThenAnotherGoBackExactlyOneMore()
    {
        var transport = new HistoryTransport();
        var service = HistoryService(transport);

        var first = await service.RunAsync("go back", scope: Adopted(PageOperation.Back));
        Assert.True(first.Completion == InteractionCompletionState.Complete, first.Detail);
        Assert.Equal(["BACK"], transport.Acts);
        Assert.Equal("https://b.example/", first.Url);

        var second = await service.RunAsync("go back one more time", scope: Adopted(PageOperation.Back, "https://b.example/"));
        Assert.Equal(InteractionCompletionState.Complete, second.Completion);
        Assert.Equal(["BACK", "BACK"], transport.Acts);
        Assert.Equal("https://a.example/", second.Url);

        var none = await service.RunAsync("go back", scope: Adopted(PageOperation.Back, "https://a.example/"));
        Assert.Equal(InteractionCompletionState.Incomplete, none.Completion);
        Assert.Equal("That tab has no previous page.", none.Detail);
    }

    [Fact]
    public async Task HistoryTraversal_KeepsTheAdoptedTabAndItsEntries()
    {
        var transport = new HistoryTransport();
        var service = HistoryService(transport);
        await service.RunAsync("go back", scope: Adopted(PageOperation.Back));
        var forward = await service.RunAsync("go forward", scope: Adopted(PageOperation.Forward, "https://b.example/"));
        Assert.Equal(["BACK", "FORWARD"], transport.Acts);
        Assert.Equal("https://c.example/", forward.Url);
        Assert.Equal(0, transport.Closes);
        Assert.Equal(3, transport.Pages.Count);
    }

    // ── planned steps: no question from uncertainty, meaningful failure ──────

    private sealed class OneStep(PlanStepKind kind, string description, string? target = null) : IBrowserStepCompiler
    {
        public int Calls { get; private set; }
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult<CompiledBrowserTask?>(new(new InteractionPlan(utterance, description,
                [new(kind, description, Target: target)]),
                new BrowserGoalNormalization(description, target, null, null, null, [], description, [], SemanticEndState.ResourceOpened)));
        }
    }

    private static JevAnswer Op(string choice, double p) => new("choice", choice, new Dictionary<string, double> { [choice] = p }, p);

    [Fact]
    public async Task LowOperationConfidence_AloneNeverAsksTheUser()
    {
        var transport = new HistoryTransport();
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Op("CLICK", .05) });
        var service = new BrowserInteractionService(transport, gateway, new OneStep(PlanStepKind.Act, "Click Go"));
        var result = await service.RunAsync("click go",
            scope: new(BrowserScopeKind.ActiveTab, 1, "https://c.example/", ExplicitSelection: true));
        Assert.NotEqual(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Null(result.Choices);
        Assert.Empty(transport.Acts);
    }

    [Fact]
    public async Task MissingTarget_FailsWithAMeaningfulReason()
    {
        var transport = new HistoryTransport();
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Op("BLOCKED", .9) });
        var service = new BrowserInteractionService(transport, gateway, new OneStep(PlanStepKind.Open, "Open Zebra", "Zebra"));
        var result = await service.RunAsync("open Zebra",
            scope: new(BrowserScopeKind.ActiveTab, 1, "https://c.example/", ExplicitSelection: true,
                EndState: SemanticEndState.ResourceOpened));
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Contains("Zebra", result.Detail);
        Assert.DoesNotContain("clarify", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BACK", transport.Acts);
    }

    [Fact]
    public void SimpleCurrentPageClick_IsFramedInCodeWithoutTheCompiler()
    {
        var scope = new BrowserExecutionScope(BrowserScopeKind.ActiveTab, 1, "https://c.example/", ExplicitSelection: true)
        {
            EndState = SemanticEndState.ResourceOpened
        };
        var simple = SimpleStepFramer.TryFrame("open the first result here", scope);
        Assert.NotNull(simple);
        Assert.Single(simple!.Plan.Steps);
        Assert.Equal(PlanStepKind.Open, simple.Plan.Current.Kind);
        // Sequencing is a genuinely multi-step request and still compiles.
        Assert.Null(SimpleStepFramer.TryFrame("find Apollo 13 and open Jim Lovell", scope));
    }
}
