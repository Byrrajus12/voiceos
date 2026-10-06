using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Audio;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Config;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class RecentTaskPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
    private static RecentTaskFrame Frame => new(7, "session", "https://example.com/", "search", InteractionCompletionState.Complete, Now);
    private static BrowserTabInfo Tab => new(7, 1, true, Frame.Url, "Example", BrowserTabProvenance.VoiceOs, Frame.SessionId);

    [Theory]
    [InlineData(TaskRelation.NewTask, false)]
    [InlineData(TaskRelation.Uncertain, false)]
    [InlineData(TaskRelation.Uncertain, true)]
    [InlineData(TaskRelation.ContinueRecent, true)]
    [InlineData(TaskRelation.RequiresRecent, true)]
    public void LowConfidenceMissingOrUncertainRelation_DoesNotDeleteOrHideFrame(TaskRelation relation, bool established)
    {
        var frame = Frame;
        var validation = RecentTaskPolicy.Validate(frame, true, [Tab], Now);
        Assert.Same(frame, validation.Stored);
        Assert.Same(frame, RecentTaskPolicy.ExposedForScope(validation.Exposed,
            new(CommandRoute.ComputerUse, 1, TaskRelation: relation) { TaskRelationEstablished = established }));
    }

    [Fact]
    public void EstablishedNewTask_HidesFrameThisTurn_ButRetainsIt()
    {
        var frame = Frame;
        var validation = RecentTaskPolicy.Validate(frame, true, [Tab], Now);
        Assert.Null(RecentTaskPolicy.ExposedForScope(validation.Exposed,
            new(CommandRoute.ComputerUse, 1) { TaskRelationEstablished = true }));
        Assert.Same(frame, validation.Stored);
        Assert.Same(frame, RecentTaskPolicy.Validate(validation.Stored, true, [Tab], Now).Exposed);
    }

    [Theory]
    [InlineData("closed")] [InlineData("url")] [InlineData("session")]
    public void TabClosedUrlOrSessionChanged_DeletesFrame_WhenInventoryKnown(string invalid)
    {
        var tabs = invalid == "closed" ? Array.Empty<BrowserTabInfo>() : new[] { Tab with {
            Url = invalid == "url" ? "https://example.com/changed" : Tab.Url,
            SessionId = invalid == "session" ? "different" : Tab.SessionId } };
        var result = RecentTaskPolicy.Validate(Frame, true, tabs, Now);
        Assert.Null(result.Stored); Assert.Null(result.Exposed);
    }

    [Fact]
    public void InventoryUnknown_RetainsButDoesNotExposeFrame()
    {
        var frame = Frame;
        var result = RecentTaskPolicy.Validate(frame, false, [], Now);
        Assert.Same(frame, result.Stored); Assert.Null(result.Exposed);
    }

    [Fact]
    public void ExpiredFrame_IsDeleted_EvenWithUnknownInventory()
    {
        var result = RecentTaskPolicy.Validate(Frame, false, [], Now + RecentTaskPolicy.Ttl + TimeSpan.FromTicks(1));
        Assert.Null(result.Stored); Assert.Null(result.Exposed);
        Assert.NotNull(RecentTaskPolicy.Validate(Frame, true, [Tab], Now + RecentTaskPolicy.Ttl).Stored);
    }

    [Theory]
    [InlineData(InteractionCompletionState.Complete)] [InlineData(InteractionCompletionState.Uncertain)]
    public void CompleteOrUncertainBrowserRun_ReplacesFrame(InteractionCompletionState state)
    {
        var result = RecentTaskPolicy.AfterBrowserRun(Frame,
            new(state, null, null, "https://new.example/", "New", TabId: 9, SessionId: "new", SemanticGoal: "new goal"), "transcript", Now);
        Assert.Equal(9, result?.TabId); Assert.Equal("new goal", result?.SemanticGoal);
    }

    [Theory]
    [InlineData(InteractionCompletionState.Incomplete, null)]
    [InlineData(InteractionCompletionState.Incomplete, UnavailableReason.IntentService)]
    [InlineData(InteractionCompletionState.Uncertain, UnavailableReason.ChromeCompanion)]
    public void IncompleteOrUnavailableRun_KeepsPreviousFrame(InteractionCompletionState state, UnavailableReason? reason)
    {
        var frame = Frame;
        Assert.Same(frame, RecentTaskPolicy.AfterBrowserRun(frame,
            new(state, null, null, "https://new.example/", "New", TabId: 9, SessionId: "new", Unavailable: reason), "new", Now));
    }

    private sealed class Gateway(string? relation, double confidence) : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken ct = default)
        {
            var answers = new Dictionary<string, JevAnswer> { ["route"] = new("choice", "COMPUTER_USE", new Dictionary<string, double>(), 1) };
            if (relation is not null) answers["task_relation"] = new("choice", relation, new Dictionary<string, double>(), confidence);
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(answers);
        }
    }

    [Theory]
    [InlineData("NewTask", .44, false, TaskRelation.NewTask)]
    [InlineData("NewTask", .45, true, TaskRelation.NewTask)]
    [InlineData("NewTask", .46, true, TaskRelation.NewTask)]
    [InlineData(null, 1, false, TaskRelation.NewTask)]
    [InlineData("bad", 1, false, TaskRelation.NewTask)]
    public async Task TaskRelationEstablished_OnlyWhenParsedAboveThreshold(string? relation, double confidence, bool established, TaskRelation expected)
    {
        var route = await new TypeSafeCommandRouter(new Gateway(relation, confidence)).RouteAsync("search");
        Assert.Equal(established, route.TaskRelationEstablished);
        Assert.Equal(expected, route.TaskRelation);
    }

    private sealed class FixedRouter : ICommandRouter
    {
        public bool Established { get; set; }
        public ValueTask<CommandRouteDecision> RouteAsync(string transcript, CancellationToken ct = default)
            => ValueTask.FromResult(new CommandRouteDecision(CommandRoute.ComputerUse, 1,
                ExplicitUrl: new("https://example.com/"), ContextDependency: ContextDependency.SelfContained,
                GoalShape: GoalShape.ActionOnSurface) { TaskRelationEstablished = Established });
    }
    private sealed class Browser : IBrowserInteractionService
    {
        private int _calls;
        public ValueTask<BrowserInteractionOutcome> RunAsync(string u, CancellationToken ct = default, string? activationId = null, BrowserExecutionScope? scope = null)
            => ValueTask.FromResult(++_calls == 1 ? new BrowserInteractionOutcome(InteractionCompletionState.Complete,
                null, null, Frame.Url, "Example", TabId: 7, SessionId: Frame.SessionId)
                : new BrowserInteractionOutcome(InteractionCompletionState.Incomplete, "unchanged", null, null, null));
        public ValueTask<BrowserInteractionOutcome> ResumeAsync(string choiceId, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Transport : IChromeCompanionTransport
    {
        public bool Connected { get; set; } = true;
        public bool FailInventory { get; set; }
        public bool IsConnected => Connected;
        public ValueTask<IReadOnlyList<BrowserTabInfo>> ListTabsAsync(CancellationToken ct = default)
            => FailInventory ? throw new HttpRequestException("offline") : ValueTask.FromResult<IReadOnlyList<BrowserTabInfo>>([Tab]);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task NewTaskRoute_RetainsRecentTaskForNextTurn_AndControlsExposure(bool established, bool inventoryUnavailable)
    {
        var router = new FixedRouter(); var transport = new Transport();
        using var o = new ActivationOrchestrator(new GlobalKeyboardHook(0xA3, NullLogger<GlobalKeyboardHook>.Instance),
            new GlobalKeyboardHook(0x77, NullLogger<GlobalKeyboardHook>.Instance), new AudioCaptureService(NullLogger<AudioCaptureService>.Instance),
            null, new VoiceOSConfig(), NullLogger<ActivationOrchestrator>.Instance,
            commandRouter: router, browserInteraction: new Browser(), browserTransport: transport);
        await o.RunTranscriptAsync("search");
        var previous = o.RecentTaskForTesting; Assert.NotNull(previous);
        router.Established = established; transport.FailInventory = inventoryUnavailable;
        var second = await o.RunTranscriptAsync("search again");
        Assert.Same(previous, o.RecentTaskForTesting);
        Assert.Equal(!established && !inventoryUnavailable, second.ExecutionContext?.RecentTask is not null);
        router.Established = false; transport.FailInventory = false;
        var third = await o.RunTranscriptAsync("continue");
        Assert.Same(previous, third.ExecutionContext?.RecentTask);
    }
}
