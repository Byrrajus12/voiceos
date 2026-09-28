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
    public async Task SelectedTab_ObservesExistingSurfaceWithoutOpeningAnother()
    {
        var session = "selected-session";
        var transport = new FakeTransport([ButtonSnapshot(session, 42, "e1")]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("search Kendrick Lamar"),
            new NeverComplete(), session, tabId: 42);

        await surface.ObserveAsync();

        Assert.Equal(0, transport.OpenCount);
        Assert.Equal(1, transport.ObserveCount);
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

        var goal = BrowserGoal.FromUtterance("find ripgrep on GitHub") with {
            Normalization = new("locate ripgrep", "ripgrep", "repository", "GitHub",
                "https://github.com/", ["ripgrep"], "repository visible", []) };
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

    [Fact]
    public async Task SelectedTab_NavigationBeforeFirstObservation_FailsClosed()
    {
        var session = "test-session-selected";
        var snapshot = ButtonSnapshot(session, 7, "e1") with { Url = "https://other.example/" };
        var transport = new FakeTransport([snapshot]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("search React"),
            new NeverComplete(), session, tabId: 7, expectedFirstUrl: "https://example.com/");

        var failure = await Assert.ThrowsAsync<ChromeCompanionException>(
            async () => await surface.ObserveAsync());
        Assert.Equal("STALE_TAB", failure.Code);
        Assert.Equal(1, transport.ObserveCount);
    }

    [Fact]
    public async Task VerifiedChildTabBecomesTheTaskSurface()
    {
        const string session = "child-session-001";
        var source = ButtonSnapshot(session, 7, "e1");
        var child = ButtonSnapshot(session, 9, "e2") with {
            Url = "https://destination.example/", AdoptedFromTabId = 7 };
        var transport = new FakeTransport([source, child, child]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open the resource"),
            new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(x => x.Actions)
            .Single(x => x.Kind == InteractionActionKind.Activate);
        Assert.True((await surface.ExecuteAsync(click, observation)).Succeeded);
        await surface.ObserveAsync();
        Assert.Equal(9, surface.TabId);
        Assert.Equal(9, transport.LastObservedTabId);
    }

    [Fact]
    public async Task UnverifiedTabTransitionStopsAsAmbiguous()
    {
        const string session = "child-session-002";
        var transport = new FakeTransport([ButtonSnapshot(session, 7, "e1"),
            ButtonSnapshot(session, 9, "e2")]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open the resource"),
            new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(x => x.Actions)
            .Single(x => x.Kind == InteractionActionKind.Activate);
        var result = await surface.ExecuteAsync(click, observation);
        Assert.Equal(InteractionResultStatus.TopologyAmbiguous, result.Status);
        Assert.Equal(7, surface.TabId);
    }

    private static InteractionAction BackAction(InteractionObservation observation)
        => observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.GoBack);

    [Fact]
    public async Task ConfirmedBack_ActionResultSnapshotReflectsPreviousPage()
    {
        var session = "back-session-a";
        var current = ButtonSnapshot(session, 7, "e1") with { CanGoBack = true };
        var previous = new BrowserSnapshot(7, session, "rev-back", "https://example.com/results", "Results",
            "text", false, Viewport, [new("e9", "link", "Result", true, false, null, null, Geo, "ctx")]);
        var transport = new FakeTransport([current, previous]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("go back"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();

        var result = await surface.ExecuteAsync(BackAction(observation), observation);

        Assert.True(result.Succeeded);
        Assert.Equal("BACK", transport.LastAction?.Action);
        var observeCount = transport.ObserveCount;
        var after = await surface.ObserveAfterActionAsync();
        Assert.Equal(observeCount, transport.ObserveCount);
        Assert.Equal("https://example.com/results", surface.LatestSnapshot?.Url);
        Assert.NotNull(after.Candidates.SingleOrDefault(c => c.Id == "e9"));
    }

    [Theory]
    [InlineData("NO_HISTORY", InteractionResultStatus.NoEffect)]
    [InlineData("NAVIGATION_NOT_OBSERVED", InteractionResultStatus.NoEffect)]
    [InlineData("NAVIGATION_UNCONFIRMED", InteractionResultStatus.PlatformFailure)]
    [InlineData("NAVIGATION_FAILED", InteractionResultStatus.PlatformFailure)]
    public async Task UnconfirmedBack_IsNeverSuccessAndDoesNotForwardASnapshot(string code,
        InteractionResultStatus expected)
    {
        var session = "back-session-b";
        var current = ButtonSnapshot(session, 7, "e1") with { CanGoBack = true };
        var transport = new FakeTransport([current, current])
        {
            ThrowOnAct = new ChromeCompanionException(code, "Going back was not confirmed.")
        };
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("go back"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();

        var result = await surface.ExecuteAsync(BackAction(observation), observation);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Status);
        var before = transport.ObserveCount;
        await surface.ObserveAfterActionAsync();
        Assert.Equal(before + 1, transport.ObserveCount);
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
        public int OpenCount { get; private set; }
        public int ObserveCount { get; private set; }
        public int ActCount { get; private set; }
        public int? LastObservedTabId { get; private set; }
        public ChromeCompanionException? ThrowOnAct { get; set; }

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default)
        {
            OpenCount++;
            return ValueTask.FromResult(Next());
        }

        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default)
        {
            ObserveCount++;
            LastObservedTabId = tabId;
            return ValueTask.FromResult(Next());
        }

        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken ct = default)
        {
            ActCount++;
            LastAction = action;
            if (ThrowOnAct is { } ex) throw ex;
            return ValueTask.FromResult(Next());
        }

        private BrowserSnapshot Next() => snapshots[Math.Min(_index++, snapshots.Length - 1)];
    }

    [Fact]
    public async Task ObserveAfterActionAsync_ReusesActReturnedSnapshot_WithoutExtraTransportObserve()
    {
        var session = "reuse-session-a";
        var initial = ButtonSnapshot(session, 7, "e1");
        var afterAct = ButtonSnapshot(session, 7, "e1") with { Revision = "rev-act" };
        var transport = new FakeTransport([initial, afterAct]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);

        var result = await surface.ExecuteAsync(click, observation);
        Assert.True(result.Succeeded);
        var observeCountBefore = transport.ObserveCount;

        var reused = await surface.ObserveAfterActionAsync();

        Assert.Equal(observeCountBefore, transport.ObserveCount);
        Assert.Equal(1, transport.ActCount);
        Assert.NotNull(reused.Candidates.SingleOrDefault(c => c.Id == "e1"));
    }

    [Fact]
    public async Task ObserveAfterActionAsync_WithNothingPending_CallsTransport()
    {
        var session = "reuse-session-b";
        var snapshot = ButtonSnapshot(session, 7, "e1");
        var transport = new FakeTransport([snapshot, snapshot, snapshot]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);
        await surface.ExecuteAsync(click, observation);

        await surface.ObserveAfterActionAsync(); // consumes the pending action snapshot
        var before = transport.ObserveCount;

        await surface.ObserveAfterActionAsync(); // nothing pending now

        Assert.Equal(before + 1, transport.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_AfterAction_IsFreshAndDiscardsPendingActionSnapshot()
    {
        var session = "reuse-session-c";
        var snapshot = ButtonSnapshot(session, 7, "e1");
        var transport = new FakeTransport([snapshot, snapshot, snapshot]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);
        await surface.ExecuteAsync(click, observation);

        var before = transport.ObserveCount;
        await surface.ObserveAsync(); // fresh, discards the pending action snapshot

        Assert.Equal(before + 1, transport.ObserveCount);

        var afterFresh = transport.ObserveCount;
        await surface.ObserveAfterActionAsync(); // nothing pending, hits transport again

        Assert.Equal(afterFresh + 1, transport.ObserveCount);
    }

    [Fact]
    public async Task ActionReturnedNavigationSnapshot_UpdatesRevision_StaleOldRevisionRejected()
    {
        var session = "nav-session-d";
        var initial = ButtonSnapshot(session, 7, "e1");
        var navigated = new BrowserSnapshot(7, session, "rev2", "https://example.com/next", "Next Page", "text",
            false, Viewport, [new("e2", "button", "Continue", true, false, null, null, Geo, "ctx")]);
        var afterNav = navigated with { Revision = "rev3" };
        var transport = new FakeTransport([initial, navigated, afterNav]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);

        var result = await surface.ExecuteAsync(click, observation);
        Assert.True(result.Succeeded);

        var reused = await surface.ObserveAfterActionAsync();
        Assert.Equal("https://example.com/next", surface.LatestSnapshot?.Url);

        var stale = await surface.ExecuteAsync(click, observation);
        Assert.Equal(InteractionResultStatus.StaleTarget, stale.Status);

        var newClick = reused.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);
        var success = await surface.ExecuteAsync(newClick, reused);
        Assert.True(success.Succeeded);
    }

    [Fact]
    public async Task FailingAction_LeavesNothingPending_SoNextObserveAfterActionHitsTransport()
    {
        var session = "fail-session-e";
        var snapshot = ButtonSnapshot(session, 7, "e1");
        var transport = new FakeTransport([snapshot, snapshot])
        {
            ThrowOnAct = new ChromeCompanionException("ELEMENT_NOT_VISIBLE", "The element is no longer visible.")
        };
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open result"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(c => c.Actions).Single(a => a.Kind == InteractionActionKind.Activate);

        var result = await surface.ExecuteAsync(click, observation);
        Assert.Equal(InteractionResultStatus.TargetUnavailable, result.Status);

        var before = transport.ObserveCount;
        await surface.ObserveAfterActionAsync();

        Assert.Equal(before + 1, transport.ObserveCount);
    }

    [Fact]
    public async Task AdoptedChildTabSnapshot_IsReusedByObserveAfterAction()
    {
        const string session = "child-session-f";
        var source = ButtonSnapshot(session, 7, "e1");
        var child = ButtonSnapshot(session, 9, "e2") with { Url = "https://destination.example/", AdoptedFromTabId = 7 };
        var transport = new FakeTransport([source, child]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("open the resource"), new NeverComplete(), session);
        var observation = await surface.ObserveAsync();
        var click = observation.Candidates.SelectMany(x => x.Actions).Single(x => x.Kind == InteractionActionKind.Activate);

        var result = await surface.ExecuteAsync(click, observation);
        Assert.True(result.Succeeded);

        var before = transport.ObserveCount;
        await surface.ObserveAfterActionAsync();

        Assert.Equal(before, transport.ObserveCount);
        Assert.Equal(9, surface.TabId);
        Assert.Equal("https://destination.example/", surface.LatestSnapshot?.Url);
    }

    [Fact]
    public async Task SearchElement_LabelIncludesPurposeSearchMarker()
    {
        var session = "purpose-search-session";
        var snapshot = new BrowserSnapshot(7, session, "rev1", "https://youtube.com/", "YouTube", "text",
            false, Viewport, [new("combo1", "combobox", "", true, true, null, null, Geo, "context", Search: true)]);
        var transport = new FakeTransport([snapshot]);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("search for KSI"), new NeverComplete(), session);

        var observation = await surface.ObserveAsync();

        var label = observation.Candidates.Single(c => c.Id == "combo1").Label;
        Assert.Contains("purpose='search'", label);
    }
}
