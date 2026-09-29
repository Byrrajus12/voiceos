using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserEffectTests
{
    private const string Session = "effects-session";
    private static readonly BrowserViewport Viewport = new(1280, 800, 0, 0);
    private static readonly BrowserGeometry Geo = new(0, 0, 100, 30, true);

    private static BrowserElement Link(string @ref, string name, string href)
        => new(@ref, "link", name, true, false, null, href, Geo, "results");
    private static BrowserElement Box(string @ref, string value = "")
        => new(@ref, "searchbox", "Search", true, true, value, null, Geo, "header");

    private static BrowserSnapshot Snap(string url, int tab = 7, string text = "page", bool back = false,
        BrowserActionSignals? signals = null, int? adoptedFrom = null, params BrowserElement[] elements)
        => new(tab, Session, "rev", url, "Title", text, false, Viewport, elements, back, adoptedFrom, signals);

    private static async Task<(InteractionObservation Before, InteractionActionResult Result, InteractionObservation After)>
        Act(BrowserSnapshot start, BrowserSnapshot after, InteractionActionKind kind, string? target = null,
            string? text = null)
    {
        var transport = new ScriptedTransport(start, after);
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("go"), new Never(), Session);
        var before = await surface.ObserveAsync();
        var action = before.Candidates.SelectMany(static c => c.Actions)
            .First(a => a.Kind == kind && (target is null || a.TargetId == target));
        if (text is not null) action = action with { Text = text };
        var result = await surface.ExecuteAsync(action, before);
        var next = await surface.ObserveAfterActionAsync();
        return (before, result, next);
    }

    [Fact]
    public async Task Click_WithUrlChange_RecordsActivatedThenNavigated_WithExactSubject()
    {
        var start = Snap("https://a.test/", elements: [Link("e1", "Docs", "https://a.test/docs")]);
        var after = Snap("https://a.test/docs", signals: new("cross_document", "load"),
            elements: [Link("e1", "Other", "https://a.test/x")]);
        var (before, result, _) = await Act(start, after, InteractionActionKind.Activate, "e1");

        Assert.Equal([EffectKind.Activated, EffectKind.Navigated], result.Effects!.Select(static e => e.Kind));
        var activated = result.Effects![0];
        Assert.Equal("e1", activated.Subject!.ElementRef);
        Assert.Equal(before.Revision, activated.Subject.ObservationRevision);
        Assert.Equal(7, activated.Subject.TabId);
        Assert.Equal(Session, activated.Subject.SessionId);
        Assert.Equal("Docs", activated.Subject.Label);
        Assert.Equal("https://a.test/docs", activated.Subject.Href);
        Assert.Equal("cross_document", activated.Get("signal"));
        var navigated = result.Effects![1];
        Assert.Equal("https://a.test/", navigated.Get("from"));
        Assert.Equal("https://a.test/docs", navigated.Get("to"));
        Assert.Equal("cross_document", navigated.Get("signal"));
    }

    [Fact]
    public async Task Click_WithChangedContentAndSameUrl_RecordsContentChanged_AndWhetherSubjectRemains()
    {
        var start = Snap("https://a.test/", text: "closed", elements: [Link("e1", "Menu", "https://a.test/m")]);
        var stays = Snap("https://a.test/", text: "open", signals: new("none"),
            elements: [Link("e1", "Menu", "https://a.test/m")]);
        var (_, kept, _) = await Act(start, stays, InteractionActionKind.Activate, "e1");
        Assert.Equal([EffectKind.Activated, EffectKind.ContentChanged], kept.Effects!.Select(static e => e.Kind));
        Assert.Equal("true", kept.Effects![1].Get("subjectPresent"));
        Assert.Equal("none", kept.Effects![1].Get("signal"));

        var gone = Snap("https://a.test/", text: "dismissed", signals: new("none"), elements: []);
        var (_, removed, _) = await Act(start, gone, InteractionActionKind.Activate, "e1");
        Assert.Equal("false", removed.Effects!.Single(static e => e.Kind == EffectKind.ContentChanged).Get("subjectPresent"));
    }

    [Fact]
    public async Task Click_WithNoObservableChange_RecordsOnlyActivated()
    {
        var page = Snap("https://a.test/", signals: new("none"), elements: [Link("e1", "Go", "https://a.test/go")]);
        var (_, result, _) = await Act(page, page with { Revision = "rev2" }, InteractionActionKind.Activate, "e1");
        Assert.Equal(EffectKind.Activated, Assert.Single(result.Effects!).Kind);
    }

    [Fact]
    public async Task Click_AdoptingAChildTab_ReportsSurfaceAcquiredOnTheNextObservation_NotNavigated()
    {
        var start = Snap("https://a.test/", elements: [Link("e1", "Wiki", "https://w.test/")]);
        var child = Snap("https://w.test/", tab: 9, signals: new("new_tab"), adoptedFrom: 7, elements: []);
        var (_, result, next) = await Act(start, child, InteractionActionKind.Activate, "e1");

        Assert.Equal(EffectKind.Activated, Assert.Single(result.Effects!).Kind);
        var acquired = Assert.Single(next.Effects!);
        Assert.Equal(EffectKind.SurfaceAcquired, acquired.Kind);
        Assert.Equal("adopted", acquired.Get("mode"));
        Assert.Equal("7", acquired.Get("fromTabId"));
        Assert.Equal(9, acquired.Subject!.TabId);
        Assert.Null(acquired.Subject.ElementRef);
        Assert.Equal(next.Revision, acquired.Subject.ObservationRevision);
    }

    [Fact]
    public async Task StartupAndPreparedObservation_ReportSurfaceAcquiredOnce()
    {
        var snap = Snap("https://a.test/", elements: [Link("e1", "Go", "https://a.test/go")]);
        var surface = new BrowserSurface(new ScriptedTransport(snap, snap), BrowserGoal.FromUtterance("go"), new Never(), Session);
        var first = await surface.ObserveAsync();
        var second = await surface.ObserveAsync();
        Assert.Equal("opened", Assert.Single(first.Effects!).Get("mode"));
        Assert.Null(second.Effects);

        var prepared = new BrowserSurface(new ScriptedTransport(snap, snap), BrowserGoal.FromUtterance("go"), new Never(), Session);
        prepared.Prepare(snap, reuseAsFirstObservation: false);
        var fresh = await prepared.ObserveAsync();
        Assert.Equal(EffectKind.SurfaceAcquired, Assert.Single(fresh.Effects!).Kind);
    }

    [Fact]
    public async Task ObservingAnExistingTab_DoesNotClaimAcquisition()
    {
        var snap = Snap("https://a.test/", elements: [Link("e1", "Go", "https://a.test/go")]);
        var surface = new BrowserSurface(new ScriptedTransport(snap, snap), BrowserGoal.FromUtterance("go"), new Never(),
            Session, tabId: 7);
        Assert.Null((await surface.ObserveAsync()).Effects);
    }

    [Theory]
    [InlineData(InteractionActionKind.SetText, "kittens", "kittens", "true")]
    [InlineData(InteractionActionKind.SetText, "kittens", "kitten", "false")]
    [InlineData(InteractionActionKind.TypeText, "cats", "old cats", "true")]
    public async Task TextActions_RecordTextSetWithReadback_AndSignalNoneIsNotAClaim(
        InteractionActionKind kind, string typed, string fieldAfter, string matched)
    {
        var start = Snap("https://a.test/", elements: [Box("e1", "old")]);
        // The companion reports signal=none for every non-navigation action.
        var after = Snap("https://a.test/", signals: new("none"), elements: [Box("e1", fieldAfter)]);
        var (_, result, _) = await Act(start, after, kind, "e1", typed);

        Assert.Equal(EffectKind.TextSet, Assert.Single(result.Effects!).Kind);
        var set = result.Effects![0];
        Assert.Equal(typed, set.Get("value"));
        Assert.Equal(fieldAfter, set.Get("readback"));
        Assert.Equal(matched, set.Get("matched"));
        Assert.Null(set.Get("signal"));
    }

    [Fact]
    public async Task TextSet_WithAnAmbiguousFieldAfterwards_OmitsReadback()
    {
        var start = Snap("https://a.test/", elements: [Box("e1")]);
        var after = Snap("https://a.test/", elements: [Box("e1", "x"), Box("e2", "y")]);
        var (_, result, _) = await Act(start, after, InteractionActionKind.SetText, "e1", "x");
        Assert.Null(result.Effects![0].Get("readback"));
        Assert.Null(result.Effects![0].Get("matched"));
    }

    [Fact]
    public async Task Scroll_ProducesNoEffects()
    {
        var start = Snap("https://a.test/", elements: [Link("e1", "Go", "https://a.test/go")]);
        var after = Snap("https://a.test/", text: "lower", signals: new("none"), elements: []);
        var (_, result, _) = await Act(start, after, InteractionActionKind.Scroll);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Effects!);
    }

    [Fact]
    public async Task Back_WhenTraversalConfirmed_RecordsHistoryMoved()
    {
        var start = Snap("https://a.test/two", back: true, elements: []);
        var after = Snap("https://a.test/one", signals: new("cross_document"), elements: []);
        var (_, result, _) = await Act(start, after, InteractionActionKind.GoBack);

        var moved = Assert.Single(result.Effects!);
        Assert.Equal(EffectKind.HistoryMoved, moved.Kind);
        Assert.Equal("https://a.test/two", moved.Get("from"));
        Assert.Equal("https://a.test/one", moved.Get("to"));
    }

    [Theory]
    [InlineData("NO_HISTORY")]
    [InlineData("NAVIGATION_NOT_OBSERVED")]
    public async Task Back_WhenCompanionConfirmsNothingHappened_RecordsNoEffectWithReason(string code)
    {
        var start = Snap("https://a.test/", back: true, elements: []);
        var transport = new ScriptedTransport(start, start) { ThrowOnAct = new ChromeCompanionException(code, "no") };
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("back"), new Never(), Session);
        var before = await surface.ObserveAsync();
        var result = await surface.ExecuteAsync(
            before.Candidates.SelectMany(static c => c.Actions).Single(static a => a.Kind == InteractionActionKind.GoBack), before);

        Assert.Equal(InteractionResultStatus.NoEffect, result.Status);
        var effect = Assert.Single(result.Effects!);
        Assert.Equal(EffectKind.NoEffect, effect.Kind);
        Assert.Equal(code, effect.Get("reason"));
    }

    [Fact]
    public async Task OtherFailures_RecordNoEffects()
    {
        var start = Snap("https://a.test/", elements: [Link("e1", "Go", "https://a.test/go")]);
        var transport = new ScriptedTransport(start, start) { ThrowOnAct = new ChromeCompanionException("STALE_ELEMENT", "gone") };
        var surface = new BrowserSurface(transport, BrowserGoal.FromUtterance("go"), new Never(), Session);
        var before = await surface.ObserveAsync();
        var result = await surface.ExecuteAsync(
            before.Candidates.SelectMany(static c => c.Actions).First(static a => a.Kind == InteractionActionKind.Activate), before);
        Assert.Equal(InteractionResultStatus.StaleTarget, result.Status);
        Assert.Null(result.Effects);
    }

    [Fact]
    public async Task ElementRefs_AreSnapshotLocal_FingerprintsIdentifyAcrossRevisions()
    {
        // "e1" is a different element on the second page; only the fingerprint tells them apart.
        var pageA = Snap("https://a.test/", elements: [Link("e1", "Alpha", "https://a.test/a")]);
        var pageB = Snap("https://a.test/b", elements: [Link("e1", "Beta", "https://a.test/b2")]);
        var (_, first, _) = await Act(pageA, pageB, InteractionActionKind.Activate, "e1");
        var (_, second, _) = await Act(pageB, pageA, InteractionActionKind.Activate, "e1");

        var a = first.Effects![0].Subject!;
        var b = second.Effects![0].Subject!;
        Assert.Equal(a.ElementRef, b.ElementRef);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Label, b.Label);
    }

    [Fact]
    public async Task Engine_RecordsEngineNoEffect_ForAnUnchangedSuccessfulBrowserAction()
    {
        var page = Snap("https://a.test/", signals: new("none"), elements: [Link("e1", "Go", "https://a.test/go")]);
        var surface = new BrowserSurface(new ScriptedTransport(page, page with { Revision = "r2" }),
            BrowserGoal.FromUtterance("go"), new Never(), Session);
        var run = await new InteractionEngine().RunAsync(new("go"), surface, new OneClick(), new InteractionBudget(5, 5, 1));

        Assert.Equal([EffectKind.SurfaceAcquired, EffectKind.Activated, EffectKind.NoEffect],
            run.Effects!.Select(static e => e.Kind));
        Assert.Equal(InteractionResultStatus.NoEffect, run.RecentHistory[0].Result.Status);
        Assert.Equal(EffectSource.EngineRule, run.Effects![2].Source);
    }

    [Fact]
    public void ReadSignals_ParsesCompanionTimings_AndToleratesAbsence()
    {
        using var full = JsonDocument.Parse("""{"timings":{"signal":"same_document","settleReason":"quiet","observed":12}}""");
        Assert.Equal(new BrowserActionSignals("same_document", "quiet"), ChromeCompanionTransport.ReadSignals(full.RootElement));
        using var none = JsonDocument.Parse("""{"timings":{"observed":12}}""");
        Assert.Null(ChromeCompanionTransport.ReadSignals(none.RootElement));
        using var missing = JsonDocument.Parse("{}");
        Assert.Null(ChromeCompanionTransport.ReadSignals(missing.RootElement));
    }

    [Theory]
    [InlineData("CLICK", true)]
    [InlineData("BACK", true)]
    [InlineData("REPLACE_TEXT", false)]
    [InlineData("INSERT_TEXT", false)]
    [InlineData("SCROLL", false)]
    public void NoneSignal_IsMeaningfulOnlyForClickAndBack(string action, bool meaningful)
        => Assert.Equal(meaningful, BrowserActionSignals.IsMeaningfulFor(action));

    private sealed class OneClick : IInteractionDecisionSource
    {
        private bool _done;
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
        {
            if (_done) return ValueTask.FromResult(InteractionDecision.Done());
            _done = true;
            return ValueTask.FromResult(InteractionDecision.Act(
                context.Observation.Candidates.SelectMany(static c => c.Actions).First(static a => a.Kind == InteractionActionKind.Activate)));
        }
    }

    private sealed class Never : IBrowserCompletionEvaluator
    {
        public ValueTask<InteractionCompletionAssessment> AssessAsync(BrowserGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> recentHistory, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Incomplete));
    }

    /// <summary>Open/observe return the start snapshot; the next ACT returns the scripted post-action snapshot.</summary>
    private sealed class ScriptedTransport(BrowserSnapshot start, BrowserSnapshot afterAct) : IChromeCompanionTransport
    {
        public ChromeCompanionException? ThrowOnAct { get; set; }
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default) => ValueTask.FromResult(start);
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default) => ValueTask.FromResult(start);
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken ct = default)
            => ThrowOnAct is { } ex ? throw ex : ValueTask.FromResult(afterAct);
    }
}
