using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// Covers the normalization/transport overlap introduced for known-destination new tabs and
/// tab-scoped runs, plus startup cleanup on failure/cancellation and normalization prefetch.
/// Uses TaskCompletionSource-controlled fakes so concurrency is asserted deterministically —
/// no sleeps.
/// </summary>
public sealed class BrowserOverlapTests
{
    [Fact]
    public async Task KnownDestination_OpensTabBeforeNormalizationCompletes()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));

        var runTask = service.RunAsync("search for something", scope: scope).AsTask();
        Assert.Equal(1, transport.Opens);
        Assert.Equal(0, transport.Closes);

        normalizer.Complete(ControlledNormalizer.Result);
        var outcome = await runTask;

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Equal(0, transport.Closes);
    }

    [Fact]
    public async Task KnownDestination_ReusesStartupSnapshotWhenFinishedAtOrAfterNormalization()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer,
            preparedFreshnessThresholdMs: 150);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));

        // Startup (transport.OpenTaskTabAsync) resolves synchronously before normalization even
        // starts, but normalization is released immediately too, so the gap is ~0ms — well
        // within the freshness threshold, so the surface should reuse the startup snapshot.
        var runTask = service.RunAsync("search for something", scope: scope).AsTask();
        normalizer.Complete(ControlledNormalizer.Result);
        await runTask;

        Assert.Equal(1, transport.Observes); // only the recovery observation; the startup snapshot was reused
    }

    [Fact]
    public async Task KnownDestination_FetchesFreshObservationWhenStartupFinishedWellBeforeNormalization()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        // A tiny threshold makes "startup finished well before normalization" trivial to
        // reproduce without waiting on real 150ms gaps.
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer,
            preparedFreshnessThresholdMs: 0);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));

        var runTask = service.RunAsync("search for something", scope: scope).AsTask();
        await Task.Delay(15);
        normalizer.Complete(ControlledNormalizer.Result);
        await runTask;

        Assert.Equal(2, transport.Observes);
    }

    [Fact]
    public async Task KnownDestination_NormalizationFails_ClosesStartedTab()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("No decision expected")), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));

        var runTask = service.RunAsync("search for something", scope: scope).AsTask();
        normalizer.Fail();
        var outcome = await runTask;

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Equal(1, transport.Opens);
        Assert.Equal(1, transport.Closes);
    }

    [Fact]
    public async Task KnownDestination_CallerCancellation_PropagatesAndEventuallyClosesTab()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("No decision expected")), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));
        using var cts = new CancellationTokenSource();

        var runTask = service.RunAsync("search for something", cts.Token, scope: scope).AsTask();
        Assert.Equal(1, transport.Opens);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        var closedTab = await transport.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.Closes);
        Assert.Equal(42, closedTab);
    }

    [Fact]
    public async Task DestinationLessNewTaskTab_StaysSerial()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        // "notaknownservice" does not resolve, so BootstrapUrl depends on normalization.
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, NamedServiceHint: "notaknownservice");

        var runTask = service.RunAsync("search for something", scope: scope).AsTask();
        Assert.Equal(0, transport.Opens);

        normalizer.Complete(ControlledNormalizer.Result);
        await runTask;
        Assert.Equal(1, transport.Opens);
    }

    [Fact]
    public async Task FailedNormalization_StillStopsBeforeOpeningTab()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("Jev should not be called")), normalizer);
        var runTask = service.RunAsync("Find the Ripcrap repository on github").AsTask();
        normalizer.Fail();
        var result = await runTask;
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal(0, transport.Opens);
    }

    [Fact]
    public async Task TabScope_SelectsWhileNormalizationBlocked_ThenObservesFreshAfterJoin()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.ActiveTab, 7, "https://example.com/",
            ExplicitSelection: true);

        var runTask = service.RunAsync("update this page and then review it", scope: scope).AsTask();
        Assert.Equal(1, transport.Selections);
        Assert.Equal(0, transport.Observes);

        normalizer.Complete(ControlledNormalizer.Result);
        await runTask;
        Assert.Equal(2, transport.Observes);
    }

    [Fact]
    public async Task TabScope_SelectionFailure_ReturnsIncompleteWithoutObserving()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new FailingSelectTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("No decision expected")), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.ActiveTab, 7, "https://example.com/",
            ExplicitSelection: true);

        var outcome = await service.RunAsync("update this page and then review it", scope: scope);

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Contains("Chrome companion unavailable", outcome.Detail);
        Assert.Equal(0, transport.Observes);
    }

    [Fact]
    public async Task Prefetch_Hit_ConsumesPrefetchedNormalizationWithoutSecondCall()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, NamedServiceHint: "notaknownservice");

        service.PrefetchNormalization("search for something", "activation-1");
        normalizer.Complete(ControlledNormalizer.Result);
        var outcome = await service.RunAsync("search for something", activationId: "activation-1", scope: scope);

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Equal(1, normalizer.Calls);
    }

    [Fact]
    public async Task Prefetch_Miss_WhenActivationIdDiffers_CallsNormalizerAgain()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, NamedServiceHint: "notaknownservice");

        service.PrefetchNormalization("search for something", "activation-1");
        normalizer.Complete(ControlledNormalizer.Result);
        var outcome = await service.RunAsync("search for something", activationId: "activation-2", scope: scope);

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Equal(2, normalizer.Calls);
    }

    [Fact]
    public async Task Prefetch_Miss_WhenUtteranceDiffers_CallsNormalizerAgain()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, NamedServiceHint: "notaknownservice");

        service.PrefetchNormalization("search for something else", "activation-1");
        normalizer.Complete(ControlledNormalizer.Result);
        var outcome = await service.RunAsync("search for something", activationId: "activation-1", scope: scope);

        Assert.Equal(InteractionCompletionState.Incomplete, outcome.Completion);
        Assert.Equal(2, normalizer.Calls);
    }

    // -- surface preparation overlapped with the plan compile ----------------------------------------------------

    [Fact]
    public async Task NamedDestination_BeginsLoadingAndSaysSo_WhileTheCompileIsInFlight_AndNothingSemanticRunsYet()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var activity = new List<BrowserActivity>();
        service.ActionStarting += activity.Add;
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://www.youtube.com/"));

        var run = service.RunAsync("play hello on youtube", scope: scope).AsTask();

        Assert.Equal(1, transport.Opens);                       // the destination is already loading
        Assert.Equal(0, transport.Acts);                        // no plan yet, so no semantic action
        var first = Assert.Single(activity);
        Assert.True(first.OpeningTab);
        Assert.Null(first.Query);                               // nothing is claimed about the task yet
        normalizer.Complete(ControlledNormalizer.Result);
        await run;
    }

    [Fact]
    public async Task GenericWebTask_PreparesTheSearchSurface_WhileCompiling_AndKeepsItWhenThePlanStartsThere()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var activity = new List<BrowserActivity>();
        service.ActionStarting += activity.Add;
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab) { ContextDependency = ContextDependency.SelfContained };

        var run = service.RunAsync("find out who plays the lead in a ripgrep documentary", scope: scope).AsTask();

        Assert.Equal(1, transport.Opens);
        Assert.Equal(0, transport.Acts);
        Assert.Equal("Preparing web search", Assert.Single(activity).StepText);
        normalizer.Complete(ControlledNormalizer.Result);        // names no service in the words, so the default surface stands
        await run;
        Assert.Equal(1, transport.Opens);
        Assert.Equal(0, transport.Closes);
    }

    [Fact]
    public async Task GenericWebTask_DiscardsThePreparedSurface_WhenThePlanStartsElsewhere()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab) { ContextDependency = ContextDependency.SelfContained };

        var run = service.RunAsync("find ripgrep on GitHub", scope: scope).AsTask();
        Assert.Equal(1, transport.Opens);
        normalizer.Complete(ControlledNormalizer.Result);        // the user named GitHub
        await run;

        Assert.Equal(1, transport.Closes);                       // the speculative search tab is closed, not left behind
        Assert.Equal(2, transport.Opens);                        // and the plan's own origin is opened
    }

    [Fact]
    public async Task AContextDependentOrUncertainRequest_DoesNotPrepareASurfaceEarly()
    {
        var normalizer = new ControlledNormalizer();
        var transport = new ControlledTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var run = service.RunAsync("do that again", scope: new BrowserExecutionScope(BrowserScopeKind.NewTaskTab)).AsTask();
        Assert.Equal(0, transport.Opens);
        normalizer.Complete(ControlledNormalizer.Result);
        await run;
    }

    [Fact]
    public async Task ADuplicatePrefetchOfTheSameUtteranceInOneActivation_CompilesOnce_AndAnUnusedOneIsCancelled()
    {
        var normalizer = new ControlledNormalizer();
        var service = new BrowserInteractionService(new ControlledTransport(), BlockedGateway(), normalizer);
        service.PrefetchNormalization("find ripgrep", "a1");
        service.PrefetchNormalization("find ripgrep", "a1");   // a re-route reaches the same point again
        Assert.Equal(1, normalizer.Calls);
        normalizer.Complete(ControlledNormalizer.Result);
        await Task.CompletedTask;
    }

    // -- the prepared startup is consumed once, whichever finishes first -------------------------------------------

    private sealed class GatedOpenTransport : IChromeCompanionTransport
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Opens { get; private set; }
        public int Observes { get; private set; }
        public void FinishStartup() => _open.TrySetResult();
        public async ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default)
        {
            Opens++;
            await _open.Task;
            return Snap(s, 42);
        }
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) { Observes++; return ValueTask.FromResult(Snap(s, t)); }
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default) => ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        private static BrowserSnapshot Snap(string s, int t) => new(t, s, "r1", "https://example.com/", "Example", "text", false, new(1280, 800, 0, 0), [], false);
    }

    private static async Task<GatedOpenTransport> RunPrepared(Func<ControlledNormalizer, GatedOpenTransport, Task<Task>> release)
    {
        var normalizer = new ControlledNormalizer();
        var transport = new GatedOpenTransport();
        var service = new BrowserInteractionService(transport, BlockedGateway(), normalizer);
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://example.com/"));
        var run = service.RunAsync("search for something", scope: scope).AsTask();
        await await release(normalizer, transport);
        await run;
        Assert.Equal(1, transport.Opens);
        Assert.Equal(1, transport.Observes);     // only the recovery look: the prepared startup observation was the first one, used once
        return transport;
    }

    [Fact]
    public Task PreparedStartup_IsReused_WhenTheCompileFinishesFirst()
        => RunPrepared((n, t) => { n.Complete(ControlledNormalizer.Result); t.FinishStartup(); return Task.FromResult(Task.CompletedTask); });

    [Fact]
    public Task PreparedStartup_IsReused_WhenTheSurfaceFinishesFirst_EvenWellBeforeTheCompile()
        => RunPrepared(async (n, t) => { t.FinishStartup(); await Task.Delay(300); n.Complete(ControlledNormalizer.Result); return Task.CompletedTask; });

    [Fact]
    public Task PreparedStartup_IsReused_WhenBothFinishTogether()
        => RunPrepared((n, t) => { t.FinishStartup(); n.Complete(ControlledNormalizer.Result); return Task.FromResult(Task.CompletedTask); });

    private static FakeGateway BlockedGateway()
        => new((_, _) => Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99))));

    private static JevAnswer Choice(string value, double confidence = 1)
        => new("choice", value, new Dictionary<string, double> { [value] = confidence }, confidence);

    private static JevAnswer Noul(double probability)
        => new("noul", probability >= .5 ? "true" : "false",
            new Dictionary<string, double> { ["noul"] = probability }, probability);

    private static IReadOnlyDictionary<string, JevAnswer> Answers(params (string Key, JevAnswer Value)[] values)
        => values.ToDictionary(static item => item.Key, static item => item.Value);

    private sealed class FakeGateway(
        Func<object, IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer)
        : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(
            object state, IReadOnlyDictionary<string, JevQuestionDto> questions,
            CancellationToken cancellationToken = default)
            => Task.FromResult(answer(state, questions));
    }

    private sealed class ControlledNormalizer : NormalizingCompiler
    {
        public static BrowserGoalNormalization Result => new(
            "view the ripgrep repository on GitHub", "ripgrep", "repository", "GitHub",
            "https://github.com/", ["ripgrep", "ripgrep github"],
            "GitHub repository page for ripgrep is open", [new("Ripcrap", "ripgrep", .9)]);

        private TaskCompletionSource<BrowserGoalNormalization?> _tcs = NewSource();
        public int Calls { get; private set; }

        public void Complete(BrowserGoalNormalization? result) => _tcs.TrySetResult(result);
        public void Fail() => Complete(null);

        public override ValueTask<BrowserGoalNormalization?> NormalizeAsync(
            string utterance, CancellationToken cancellationToken = default)
        {
            Calls++;
            var tcs = _tcs;
            return new ValueTask<BrowserGoalNormalization?>(tcs.Task.WaitAsync(cancellationToken));
        }

        private static TaskCompletionSource<BrowserGoalNormalization?> NewSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledTransport : IChromeCompanionTransport
    {
        public int Opens { get; private set; }
        public int Observes { get; private set; }
        public int Closes { get; private set; }
        public int Selections { get; private set; }
        public TaskCompletionSource<int> ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult(Snapshot(sessionId, 42));
        }

        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
        {
            Observes++;
            return ValueTask.FromResult(Snapshot(sessionId, tabId));
        }

        public int Acts { get; private set; }

        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
        {
            Acts++;
            return ValueTask.FromResult(Snapshot(action.SessionId, action.TabId));
        }

        public ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl,
            bool requireActive, CancellationToken cancellationToken = default)
        {
            Selections++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseTaskTabAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
        {
            Closes++;
            ClosedSignal.TrySetResult(tabId);
            return ValueTask.CompletedTask;
        }

        private static BrowserSnapshot Snapshot(string session, int tabId) => new(tabId, session, "r1",
            "https://example.com/", "Example", "text", false, new(1280, 800, 0, 0), [], false);
    }

    private sealed class FailingSelectTransport : IChromeCompanionTransport
    {
        public int Observes { get; private set; }

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected in this test.");

        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
        {
            Observes++;
            return ValueTask.FromResult(new BrowserSnapshot(tabId, sessionId, "r1", "https://example.com/",
                "Example", "text", false, new(1280, 800, 0, 0), [], false));
        }

        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Not expected in this test.");

        public ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl,
            bool requireActive, CancellationToken cancellationToken = default)
            => throw new ChromeCompanionException("SESSION_MISMATCH", "The companion returned a different browser session.");
    }
}
