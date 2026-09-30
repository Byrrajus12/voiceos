using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Candidates;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserStepCompilerTests
{
    private const string GitHubPlan = """
        {"finalGoal":"Open the Tauri repository found by searching GitHub for a Rust GUI framework",
         "endState":"ResourceOpened","resourceType":"repository","preferredService":"GitHub",
         "preferredServiceUrl":"https://github.com/",
         "steps":[
          {"kind":"Reach","description":"Go to GitHub","query":null,"target":"GitHub","progress":"Opening GitHub"},
          {"kind":"Search","description":"Search GitHub for Rust GUI framework","query":"Rust GUI framework","target":null,"progress":"Searching GitHub"},
          {"kind":"Locate","description":"Find the Tauri repository in the results","query":null,"target":"Tauri repository","progress":"Looking for Tauri"},
          {"kind":"Open","description":"Open the Tauri repository","query":null,"target":"Tauri repository","progress":"Opening Tauri"}],
         "correctedTerms":[]}
        """;

    [Fact]
    public void Parse_MultiStepRequest_KeepsEveryIntermediateIntentInOrder()
    {
        var compiled = OpenRouterBrowserStepCompiler.Parse(GitHubPlan, "Go to GitHub, search for a Rust GUI framework and open the Tauri repository.");
        Assert.NotNull(compiled);
        Assert.Equal([PlanStepKind.Reach, PlanStepKind.Search, PlanStepKind.Locate, PlanStepKind.Open],
            compiled!.Plan.Steps.Select(s => s.Kind));
        Assert.Equal("Rust GUI framework", compiled.Plan.Steps[1].Query);
        Assert.Contains("Tauri", compiled.Plan.Steps[3].Target);
        Assert.Equal("Go to GitHub, search for a Rust GUI framework and open the Tauri repository.", compiled.Plan.OriginalGoal);
        // The flat typed goal still derives from the plan for bootstrap and completion evidence.
        Assert.Equal(["Rust GUI framework"], compiled.Normalization!.SearchQueries);
        Assert.Equal("Tauri repository", compiled.Normalization.Entity);
        Assert.Equal(SemanticEndState.ResourceOpened, compiled.Normalization.EndState);
    }

    [Theory]
    [InlineData("""{"finalGoal":"g","endState":"ResultsVisible","resourceType":null,"preferredService":null,"preferredServiceUrl":null,"steps":[{"kind":"Search","description":"search","query":null,"target":null,"progress":null}],"correctedTerms":[]}""")]
    [InlineData("""{"finalGoal":"g","endState":"ResourceOpened","resourceType":null,"preferredService":null,"preferredServiceUrl":null,"steps":[{"kind":"Open","description":"open it","query":null,"target":null,"progress":null}],"correctedTerms":[]}""")]
    [InlineData("""{"finalGoal":"g","endState":"ResourceOpened","resourceType":null,"preferredService":null,"preferredServiceUrl":null,"steps":[],"correctedTerms":[]}""")]
    public void Parse_RejectsStepsThatLackTheirGroundedField(string json)
        => Assert.Null(OpenRouterBrowserStepCompiler.Parse(json, "x"));

    private sealed class CountingCompiler : IBrowserStepCompiler
    {
        public int Calls { get; private set; }
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(OpenRouterBrowserStepCompiler.Parse(GitHubPlan, utterance));
        }
    }

    private sealed class PageTransport : IChromeCompanionTransport
    {
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(sessionId, 7));
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(sessionId, tabId));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(action.SessionId, action.TabId));
        public ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl, bool requireActive,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        private static BrowserSnapshot Snapshot(string session, int tabId) => new(tabId, session, "r1",
            "https://example.com/", "Example", "text", false, new(1280, 800, 0, 0), [], false);
    }

    private sealed class BlockedGateway : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "BLOCKED", new Dictionary<string, double> { ["BLOCKED"] = .99 }, .99),
                ["stuck"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .99 }, .99)
            });
    }

    private static BrowserExecutionScope ActiveTab => new(BrowserScopeKind.ActiveTab, 7, "https://example.com/",
        EndState: SemanticEndState.StateChanged, GoalShape: GoalShape.ActionOnSurface);

    [Theory]
    [InlineData("scroll down")]
    [InlineData("click Support")]
    [InlineData("go back")]
    public async Task SimpleCurrentPageRequest_BypassesTheCompiler(string utterance)
    {
        var compiler = new CountingCompiler();
        var service = new BrowserInteractionService(new PageTransport(), new BlockedGateway(), compiler: compiler);
        await service.RunAsync(utterance, scope: ActiveTab);
        Assert.Equal(0, compiler.Calls);
    }

    [Fact]
    public async Task SequencedRequest_OnTheSameSurface_IsCompiled()
    {
        var compiler = new CountingCompiler();
        var service = new BrowserInteractionService(new PageTransport(), new BlockedGateway(), compiler: compiler);
        await service.RunAsync("click Support and then scroll down", scope: ActiveTab);
        Assert.Equal(1, compiler.Calls);
    }

    [Fact]
    public void NewTaskWithADestination_IsNeverFramedAsSimple()
    {
        var route = new CommandRouteDecision(CommandRoute.ComputerUse, .9, DestinationKind: SemanticDestinationKind.KnownService,
            DestinationName: "GitHub", GoalShape: GoalShape.ActionOnSurface);
        Assert.False(SimpleStepFramer.MayBeSimple(route, "search GitHub for tauri"));
        var current = new CommandRouteDecision(CommandRoute.ComputerUse, .9, GoalShape: GoalShape.ActionOnSurface);
        Assert.True(SimpleStepFramer.MayBeSimple(current, "click Support"));
    }
}

public sealed class SurfacePlacementTests
{
    private sealed class Chooser : IContextualScopeDecisionSource
    {
        public int ReferentCalls;
        public ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
            ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ContextualSurface.Clarify);
        public ValueTask<ReferentChoice> SelectReferentAsync(string utterance, IReadOnlyList<ReferentCandidate> candidates,
            CancellationToken cancellationToken = default)
        { ReferentCalls++; return ValueTask.FromResult(new ReferentChoice(ReferentChoiceKind.Unavailable)); }
    }

    private static readonly VoiceOS.Core.Candidates.WindowCandidate Chrome = new("w", "chrome", "Chrome", true, 42);
    private static ExecutionContextSnapshot Context(params Referent[] referents)
        => new(Chrome, [Chrome], true, [new BrowserTabInfo(3, 1, true, "https://example.org/page", "Page", BrowserTabProvenance.User)])
        { Referents = referents };

    [Fact]
    public async Task CurrentPageRequest_StaysOnTheActiveTab_EvenWithOldReferentsAround()
    {
        var old = ReferentFixtures.OldItem();
        var chooser = new Chooser();
        var route = new CommandRouteDecision(CommandRoute.ComputerUse, .9, TaskRelation: TaskRelation.NewTask,
            ContextDependency: ContextDependency.RequiresCurrentSurface, GoalShape: GoalShape.ActionOnSurface,
            MediaRequestKind: MediaRequestKind.None) { TaskRelationEstablished = true };
        var scope = await new ScopeResolver().ResolveAsync("click support", route, Context(old), chooser);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser!.Kind);
        Assert.Equal(3, scope.Browser.TabId);
        Assert.Equal(0, chooser.ReferentCalls); // a fresh command never consults history
    }

    [Fact]
    public async Task FreshSelfContainedTask_OpensItsOwnTab_InsteadOfReusingTheVisiblePage()
    {
        var chooser = new Chooser();
        var route = new CommandRouteDecision(CommandRoute.ComputerUse, .9, DestinationKind: SemanticDestinationKind.KnownService,
            DestinationName: "GitHub", TaskRelation: TaskRelation.NewTask, ContextDependency: ContextDependency.SelfContained,
            GoalShape: GoalShape.ActionOnSurface, MediaRequestKind: MediaRequestKind.None) { TaskRelationEstablished = true };
        var scope = await new ScopeResolver().ResolveAsync("search GitHub for tauri", route, Context(ReferentFixtures.OldItem()), chooser);
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser!.Kind);
        Assert.Equal("github.com", scope.Browser.Destination!.Host);
        Assert.Equal(0, chooser.ReferentCalls);
    }
}

internal static class ReferentFixtures
{
    public static Referent OldItem() => VoiceOS.Core.Tests.Interaction.ReferentStoreTests.Item("https://old.example/item", "Old item") with { Seq = 1 };
}

public sealed class UndecidedPlacementTests
{
    private sealed class Undecided : IContextualScopeDecisionSource
    {
        public ValueTask<ContextualSurface> SelectAsync(string u, CommandRouteDecision i, ExecutionContextSnapshot c,
            IReadOnlyList<ContextualSurface> o, CancellationToken t = default) => ValueTask.FromResult(ContextualSurface.Clarify);
    }

    [Fact]
    public async Task FreshNamedTask_WhenThePickerCannotDecide_GetsItsOwnTab_NotAQuestion()
    {
        var chrome = new WindowCandidate("w", "chrome", "Chrome", true, 42);
        var context = new ExecutionContextSnapshot(chrome, [chrome], true,
            [new BrowserTabInfo(3, 1, true, "https://example.org/", "Example", BrowserTabProvenance.User)]);
        var route = new CommandRouteDecision(CommandRoute.ComputerUse, .98, RequestedEntity: RequestedEntityKind.NamedEntity,
            TaskRelation: TaskRelation.NewTask, ContextDependency: ContextDependency.Uncertain, GoalShape: GoalShape.SurfaceOnly,
            MediaRequestKind: MediaRequestKind.None) { TaskRelationEstablished = true };
        var scope = await new ScopeResolver().ResolveAsync("go to npm and search for express", route, context, new Undecided());
        Assert.Equal(ExecutionScopeKind.Browser, scope.Kind);
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser!.Kind);
    }
}
