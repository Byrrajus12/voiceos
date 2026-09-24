using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserSurfaceTests
{
    private static readonly BrowserViewport Viewport = new(1280, 800, 0, 0);
    private static readonly BrowserGeometry Geo = new(0, 0, 100, 30, true);

    [Fact]
    public async Task StaleRevision_IsRejectedAfterFreshObserve()
    {
        var sessionId = "test-session-a001";
        var snapshot = ButtonSnapshot(sessionId, 42, "e1");
        var transport = new FakeTransport([snapshot, snapshot, snapshot]);

        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), sessionId);

        var first = await surface.ObserveAsync();
        var _ = await surface.ObserveAsync(); // invalidates first revision

        var clickAction = first.Candidates
            .SelectMany(static c => c.Actions)
            .First(static a => a.Kind == InteractionActionKind.Activate);
        var result = await surface.ExecuteAsync(clickAction, first);
        Assert.Equal(InteractionResultStatus.StaleTarget, result.Status);
    }

    [Fact]
    public async Task OutOfBoundsText_IsRejectedWithScopeViolation()
    {
        var sessionId = "test-session-b002";
        var snapshot = EditableSnapshot(sessionId, 7, "q");
        var transport = new FakeTransport([snapshot]);

        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("find ripgrep on GitHub"), new NeverComplete(), sessionId);
        var observation = await surface.ObserveAsync();

        var typeAction = observation.Candidates
            .SelectMany(static c => c.Actions)
            .First(static a => a.Kind == InteractionActionKind.SetText);
        var tampered = typeAction with { Text = new string('x', 241) };

        var result = await surface.ExecuteAsync(tampered, observation);
        Assert.Equal(InteractionResultStatus.ScopeViolation, result.Status);
    }

    [Fact]
    public async Task TrustedText_IsForwardedToTransport()
    {
        var sessionId = "test-session-c003";
        var snapshot = EditableSnapshot(sessionId, 7, "q");
        var transport = new FakeTransport([snapshot, snapshot]);

        var goal = BrowserGoal.FromUtterance("find ripgrep on GitHub");
        var surface = new BrowserSurface(transport, goal, new NeverComplete(), sessionId);
        var observation = await surface.ObserveAsync();

        var trustedText = BrowserTextCandidates.From(goal).First().Text;
        var typeAction = observation.Candidates
            .SelectMany(static c => c.Actions)
            .First(static a => a.Kind == InteractionActionKind.SetText);
        var withTrustedText = typeAction with { Text = trustedText };

        var result = await surface.ExecuteAsync(withTrustedText, observation);
        Assert.Equal(InteractionResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task SessionMismatch_ThrowsOnFirstObserve()
    {
        var sessionId = "test-session-d004";
        var wrongSnapshot = ButtonSnapshot("different-session-xyz", 55, "e1");
        var transport = new FakeTransport([wrongSnapshot]);

        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("click something"), new NeverComplete(), sessionId);

        await Assert.ThrowsAsync<ChromeCompanionException>(() => surface.ObserveAsync().AsTask());
    }

    [Fact]
    public async Task NullText_ForTextAction_IsRejectedWithScopeViolation()
    {
        var sessionId = "test-session-e005";
        var snapshot = EditableSnapshot(sessionId, 7, "q");
        var transport = new FakeTransport([snapshot]);

        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("find ripgrep on GitHub"), new NeverComplete(), sessionId);
        var observation = await surface.ObserveAsync();

        var typeAction = observation.Candidates
            .SelectMany(static c => c.Actions)
            .First(static a => a.Kind == InteractionActionKind.SetText);
        // null text — text was never supplied
        var result = await surface.ExecuteAsync(typeAction, observation);
        Assert.Equal(InteractionResultStatus.ScopeViolation, result.Status);
    }

    [Fact]
    public async Task EmptyEditableField_OffersOnlyReplaceText()
    {
        var session = "test-session-empty";
        var surface = new BrowserSurface(new FakeTransport([EditableSnapshot(session, 7, "q")]),
            BrowserGoal.FromUtterance("search for ripgrep"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var actions = observation.Candidates.Single(c => c.Id == "q").Actions;
        Assert.Single(actions);
        Assert.Equal(InteractionActionKind.SetText, actions[0].Kind);
    }

    [Fact]
    public async Task PopulatedEditableField_OffersReplaceAndInsert()
    {
        var session = "test-session-populated";
        var snapshot = EditableSnapshot(session, 7, "q") with
        { Elements = [EditableSnapshot(session, 7, "q").Elements[0] with { Value = "prior text" }] };
        var surface = new BrowserSurface(new FakeTransport([snapshot]),
            BrowserGoal.FromUtterance("search for ripgrep"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        Assert.Equal([InteractionActionKind.SetText, InteractionActionKind.TypeText],
            observation.Candidates.Single(c => c.Id == "q").Actions.Select(a => a.Kind));
    }

    [Fact]
    public async Task PageScroll_ExecutesWithoutElementReference()
    {
        var session = "test-session-scroll";
        var snapshot = ButtonSnapshot(session, 7, "e1");
        var transport = new FakeTransport([snapshot, snapshot]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("find result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var scroll = observation.Candidates.Single(c => c.Id == "page").Actions
            .Single(a => a.Kind == InteractionActionKind.Scroll && a.Direction == "down");

        var result = await surface.ExecuteAsync(scroll, observation);

        Assert.True(result.Succeeded);
        Assert.Equal("SCROLL", transport.LastAction?.Action);
        Assert.Null(transport.LastAction?.ElementRef);
        Assert.Equal("down", transport.LastAction?.Direction);
    }

    private static BrowserSnapshot ButtonSnapshot(string sessionId, int tabId, string elemRef)
        => new(tabId, sessionId, "rev1", "https://example.com/", "Test Page", "Some visible text",
            false, Viewport, [new(elemRef, "button", "Click Me", true, false, null, null, Geo, "test context")]);

    private static BrowserSnapshot EditableSnapshot(string sessionId, int tabId, string elemRef)
        => new(tabId, sessionId, "rev1", "https://example.com/", "Test Page", "Some visible text",
            false, Viewport, [new(elemRef, "searchbox", "Search", true, true, null, null, Geo, "search context")]);

    private sealed class NeverComplete : IBrowserCompletionEvaluator
    {
        public ValueTask<InteractionCompletionAssessment> AssessAsync(
            BrowserGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> recentHistory,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Incomplete));
    }

    private sealed class FakeTransport(BrowserSnapshot[] snapshots) : IChromeCompanionTransport
    {
        private int _index;
        public BrowserActionRequest? LastAction { get; private set; }

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default)
            => ValueTask.FromResult(Next());

        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default)
            => ValueTask.FromResult(Next());

        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken ct = default)
        {
            LastAction = action;
            return ValueTask.FromResult(Next());
        }

        private BrowserSnapshot Next() => snapshots[Math.Min(_index++, snapshots.Length - 1)];
    }
}
