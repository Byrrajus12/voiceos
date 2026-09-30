using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class InfrastructureUnavailableTests
{
    private static InfrastructureUnavailableException Unavailable(UnavailableReason reason = UnavailableReason.IntentService)
        => new(reason, ActivityMessage.ForUnavailable(reason));
    private sealed class HttpResponse(string body = "{}", HttpStatusCode status = HttpStatusCode.OK, bool timeout = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => timeout ? throw new TaskCanceledException("timeout")
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task DirectEngine_TransportOrEnvelopeFailure_SetsProviderFailed(bool http)
    {
        var engine = new TypeSafeJevDecisionEngine("test", new HttpClient(new HttpResponse(status:
            http ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, body: "invalid json")), "test", .35, .4,
            NullLogger<TypeSafeJevDecisionEngine>.Instance);
        Assert.True((await engine.DecideAsync(new("test", "", [], [], [], []))).ProviderFailed);
    }

    [Fact]
    public async Task BrowserService_DecisionProviderUnavailable_OutcomeUnavailable()
    {
        var service = new BrowserInteractionService(new Transport(), new Gateway(), new Normalizer(false));
        var result = await service.RunAsync("search");
        Assert.Equal(UnavailableReason.IntentService, result.Unavailable);
        Assert.Null(result.Choices);
    }

    private sealed class Gateway(bool completeFirst = false) : IJevGateway
    {
        private int _calls;
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken ct = default)
            => completeFirst && _calls++ == 0 ? Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(
                new Dictionary<string, JevAnswer> { ["operation"] = new("choice", "DONE", new Dictionary<string, double>(), 1),
                    ["goal_achieved"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .99 }, .99) })
                : throw new HttpRequestException("offline");
    }
    private static InteractionObservation Page => new(1, "page", """{"current_url":"https://example.com/","title":"Example","visible_text":"example"}""", []);

    [Fact]
    public async Task DecisionSource_GatewayThrows_ThrowsUnavailable()
    {
        var source = new TypeSafeBrowserDecisionSource(new Gateway(), new("search"));
        var ex = await Assert.ThrowsAsync<InfrastructureUnavailableException>(() => source.DecideAsync(
            new(new("search"), Page, [], new(), new(0, 0, 0, 0))).AsTask());
        Assert.Equal(UnavailableReason.IntentService, ex.Reason);
    }

    [Fact]
    public async Task DecisionSource_ConfirmationThrows_ThrowsUnavailable()
    {
        var source = new TypeSafeBrowserDecisionSource(new Gateway(true), new("search"));
        await source.DecideAsync(new(new("search"), Page, [], new(), new(0, 0, 0, 0)));
        await Assert.ThrowsAsync<InfrastructureUnavailableException>(() => source.AssessAsync(new("search"), Page, []).AsTask());
    }

    private sealed class LoopSurface : IInteractionSurface
    {
        public static readonly InteractionAction Action = new("click", InteractionActionKind.Activate, "e1");
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Page with { Candidates = [new("e1", "button", [Action])] });
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation, CancellationToken ct = default)
            => throw Unavailable();
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal, InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> history, CancellationToken ct = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Incomplete));
    }
    private sealed class LoopDecision(bool throws) : IInteractionDecisionSource
    {
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
            => throws ? throw Unavailable() : ValueTask.FromResult(InteractionDecision.Act(LoopSurface.Action));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InteractionEngine_PropagatesUnavailable_FromDecideOrExecute(bool decide)
        => await Assert.ThrowsAsync<InfrastructureUnavailableException>(() => new InteractionEngine().RunAsync(new("click"), new LoopSurface(), new LoopDecision(decide)).AsTask());

    private sealed class Normalizer(bool fail) : NormalizingCompiler
    {
        public override ValueTask<BrowserGoalNormalization?> NormalizeAsync(string u, CancellationToken ct = default)
            => fail ? throw Unavailable(UnavailableReason.BrowserGoalService) : ValueTask.FromResult<BrowserGoalNormalization?>(new("search", "x", null, null, null, ["x"], "results", [], SemanticEndState.ResultsVisible));
    }
    private sealed class Transport(string? actionError = null, bool observeError = false, bool selectError = false) : IChromeCompanionTransport
    {
        public int Closes { get; private set; }
        public bool IsConnected => true;
        private static BrowserSnapshot Snapshot(string session) => new(1, session, "r1", "https://example.com/", "Example", "Example", false,
            new(1280, 800, 0, 0), [new("e1", "button", "Go", true, false, null, null, new(0, 0, 40, 40, true), "Go")]);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default) => ValueTask.FromResult(Snapshot(sessionId));
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default)
            => observeError ? throw new ChromeCompanionException("TRANSPORT_DISCONNECTED", "offline") : ValueTask.FromResult(Snapshot(sessionId));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken ct = default)
            => actionError is { } code ? throw new ChromeCompanionException(code, "failed") : ValueTask.FromResult(Snapshot(a.SessionId));
        public ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl, bool requireActive, CancellationToken ct = default)
            => selectError ? throw new ChromeCompanionException("TRANSPORT_DISCONNECTED", "offline") : ValueTask.CompletedTask;
        public ValueTask CloseTaskTabAsync(string sessionId, int tabId, CancellationToken ct = default) { Closes++; return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData("TRANSPORT_DISCONNECTED")] [InlineData("TAB_TOPOLOGY_AMBIGUOUS")]
    public async Task BrowserSurface_TransportDisconnectedThrows_TopologyStillAmbiguous(string code)
    {
        var surface = new BrowserSurface(new Transport(code), new("click"), new TypeSafeBrowserDecisionSource(new Gateway(), new("click")), "session");
        var page = await surface.ObserveAsync();
        var action = page.Candidates.SelectMany(x => x.Actions).First(x => x.Kind == InteractionActionKind.Activate);
        if (code == "TRANSPORT_DISCONNECTED")
        {
            var ex = await Assert.ThrowsAsync<InfrastructureUnavailableException>(() => surface.ExecuteAsync(action, page).AsTask());
            Assert.Equal(UnavailableReason.ChromeCompanion, ex.Reason);
        }
        else Assert.Equal(InteractionResultStatus.TopologyAmbiguous, (await surface.ExecuteAsync(action, page)).Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrowserService_NormalizerUnavailable_OutcomeUnavailable_AndClosesStartupTab(bool scoped)
    {
        var transport = new Transport();
        var service = new BrowserInteractionService(transport, new Gateway(), new Normalizer(true));
        var scope = scoped ? new BrowserExecutionScope(BrowserScopeKind.ActiveTab, TabId: 1, ExpectedUrl: "https://example.com/")
            : new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new("https://example.com/"));
        var outcome = await service.RunAsync("search and then open the first result", scope: scope);
        Assert.Equal(UnavailableReason.BrowserGoalService, outcome.Unavailable);
        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Null(outcome.Choices); Assert.Equal(scoped ? 0 : 1, transport.Closes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrowserService_CompanionDisconnectedAtObserveOrSelect_OutcomeUnavailable(bool select)
    {
        var service = new BrowserInteractionService(new Transport(observeError: !select, selectError: select), new Gateway(), new Normalizer(false));
        var result = await service.RunAsync("search", scope: new(BrowserScopeKind.ActiveTab, TabId: 1, ExpectedUrl: "https://example.com/"));
        Assert.Equal(UnavailableReason.ChromeCompanion, result.Unavailable);
    }

    private sealed class Router(bool throws = false, bool scopeThrows = false, bool native = false) : ICommandRouter, IContextualScopeDecisionSource
    {
        public ValueTask<CommandRouteDecision> RouteAsync(string u, CancellationToken ct = default)
            => throws ? throw new HttpRequestException("offline") : ValueTask.FromResult(new CommandRouteDecision(
                native ? CommandRoute.NativeInteraction : scopeThrows ? CommandRoute.Clarify : CommandRoute.DirectCapability, 1,
                DestinationKind: native ? SemanticDestinationKind.KnownService : SemanticDestinationKind.None,
                DestinationName: native ? "Spotify" : null,
                SurfacePreference: native ? SurfacePreference.Native : SurfacePreference.Unspecified));
        public ValueTask<ContextualSurface> SelectAsync(string u, CommandRouteDecision intent, ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken ct = default)
            => throw new HttpRequestException("offline");
    }
    private sealed class DirectEngine : IDecisionEngine
    {
        public Task<DecisionResult> DecideAsync(DecisionState state, CancellationToken ct = default)
            => Task.FromResult(new DecisionResult(new(VoiceAction.Rejected), null, new Dictionary<string, JevAnswer>(), 0, 0, 0) { ProviderFailed = true });
    }
    private sealed class Catalog : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetAll() => [new("spotify", "Spotify", "spotify", AppLaunchKind.Win32, "spotify", null, null)];
        public AppEntry? FindById(string id) => GetAll().FirstOrDefault(x => x.Id == id);
    }
    private static ActivationOrchestrator Orchestrator(ICommandRouter router, IDecisionEngine? engine = null) => new(
        new GlobalKeyboardHook(0xA3, NullLogger<GlobalKeyboardHook>.Instance), new GlobalKeyboardHook(0x77, NullLogger<GlobalKeyboardHook>.Instance),
        new AudioCaptureService(NullLogger<AudioCaptureService>.Instance), null, new VoiceOSConfig(), NullLogger<ActivationOrchestrator>.Instance,
        commandRouter: router, decisionEngine: engine, catalog: new Catalog());

    [Theory]
    [InlineData("router")] [InlineData("direct")] [InlineData("scope")]
    public async Task Orchestrator_InfrastructureFailure_PublishesUnavailable_NotNeedsChoice(string stage)
    {
        using var o = Orchestrator(new Router(stage == "router", stage == "scope"), stage == "direct" ? new DirectEngine() : null);
        var ui = new List<ProductUiLifecycle>(); o.ProductUiChanged += (_, e) => ui.Add(e);
        var run = await o.RunTranscriptAsync("do something");
        Assert.Equal("Unavailable", run.Outcome);
        Assert.Equal(ApplicationInteractionPhase.Unavailable, run.TerminalSnapshot?.Phase);
        Assert.Equal(UnavailableReason.IntentService, run.TerminalSnapshot?.Unavailable);
        Assert.DoesNotContain(ui, x => x.Phase == ProductUiPhase.Clarify);
        Assert.Contains(ui, x => x.Phase == ProductUiPhase.Error);
    }

    [Theory]
    [InlineData(UnavailableReason.IntentService, "Can't reach the command service.")]
    [InlineData(UnavailableReason.BrowserGoalService, "Browser help is unavailable right now.")]
    [InlineData(UnavailableReason.ChromeCompanion, "Chrome companion isn't connected.")]
    public void ForUnavailable_MessagesAreSpecific(UnavailableReason reason, string message)
        => Assert.Equal(message, ActivityMessage.ForUnavailable(reason));

    [Fact]
    public async Task NativeNotEnabled_OutcomeIsUnsupported()
    {
        using var o = Orchestrator(new Router(native: true));
        var run = await o.RunTranscriptAsync("click the app control");
        Assert.Equal("Unsupported", run.Outcome);
        Assert.Equal(ApplicationInteractionPhase.Failed, run.TerminalSnapshot?.Phase);
    }
}
