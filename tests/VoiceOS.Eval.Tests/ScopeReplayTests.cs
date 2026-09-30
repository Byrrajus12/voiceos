using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Eval.Live;
using Xunit;
using Xunit.Abstractions;

namespace VoiceOS.Eval.Tests;

/// <summary>
/// Offline scope replay over eval run 20260926-185134-4823f3: the recorded route heads and
/// initial tab/foreground state of every turn are fed into <see cref="ScopeResolver"/>. The real
/// router's named-tab mapping runs over the recorded picker answer from product.log; the
/// contextual surface picker (whose answers were not logged) replays the surface the recorded
/// scope implies. No model is called.
/// </summary>
public sealed class ScopeReplayTests(ITestOutputHelper output)
{
    private sealed record Pick(string Choice, double Confidence);
    private sealed record Foreground(string ProcessName, string Title, long Hwnd);
    private sealed record ReplayCase(string ScenarioId, int Turn, string Transcript, string ActivationId,
        bool ReroutedFromDirect, RouteInfo Route, Foreground? Foreground, bool BrowserConnected,
        IReadOnlyList<TabInfo> Tabs, ScopeInfo? RecordedScope, Pick? RecordedNamedTabPick)
    {
        public string Key => $"{ScenarioId}#{Turn}";
        public bool NamedTabClaim => Route.TabDisposition == TabDisposition.ExistingNamedTab
            || Route.DestinationKind == SemanticDestinationKind.NamedTab;
    }
    private sealed record Fixture(string RunId, IReadOnlyList<ReplayCase> Cases);

    private sealed record Replayed(ReplayCase Case, ExecutionScopeDecision Scope, int TabCalls, int SurfaceCalls,
        int AppCalls);

    private static readonly Lazy<IReadOnlyList<Replayed>> All = new(() => Load().Cases
        .Select(c => ReplayAsync(c).GetAwaiter().GetResult()).ToArray());

    private static Fixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "scope-replay-20260926-185134-4823f3.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), options)!;
    }

    private static CommandRouteDecision ToRoute(RouteInfo r) => new(r.Route, r.Confidence, r.Detail, r.Reason,
        r.MediaOperation, r.MediaRequestKind, r.DestinationKind, r.DestinationName,
        r.ExplicitUrl is null ? null : new Uri(r.ExplicitUrl), r.TabDisposition, r.SurfacePreference, r.EndState,
        r.TaskRelation, r.NamedTabTarget, r.GoalShape, r.ContextDependency, r.RequestedEntity);

    private static async Task<Replayed> ReplayAsync(ReplayCase c)
    {
        var foreground = c.Foreground is null ? null
            : new WindowCandidate("fg", c.Foreground.ProcessName, c.Foreground.Title, true, (nint)c.Foreground.Hwnd);
        var tabs = c.Tabs.Select(t => new BrowserTabInfo(t.TabId, t.WindowId, t.Active, t.Url, t.Title,
            t.Provenance, t.SessionId, t.LastUsedSequence)).ToArray();
        var context = new ExecutionContextSnapshot(foreground, foreground is null ? [] : [foreground],
            c.BrowserConnected, tabs);
        var gateway = new RecordedGateway(c);
        var scope = await new ScopeResolver().ResolveAsync(c.Transcript, ToRoute(c.Route), context,
            new TypeSafeCommandRouter(gateway));
        return new(c, scope, gateway.TabCalls, gateway.SurfaceCalls, gateway.AppCalls);
    }

    /// <summary>Answers only with recorded data: the logged named-tab choice, and the contextual
    /// surface the recorded scope implies.</summary>
    private sealed class RecordedGateway(ReplayCase c) : IJevGateway
    {
        public int TabCalls { get; private set; }
        public int SurfaceCalls { get; private set; }
        public int AppCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            JevAnswer Choice(string value, double confidence) => new("choice", value, new Dictionary<string, double>(), confidence);
            IReadOnlyDictionary<string, JevAnswer> Answer(string head, JevAnswer answer)
                => new Dictionary<string, JevAnswer> { [head] = answer };
            if (questions.ContainsKey("tab"))
            {
                TabCalls++;
                // No recorded pick: the original run never asked, so nothing is known.
                var pick = c.RecordedNamedTabPick;
                return Task.FromResult(Answer("tab", pick is null ? Choice("NONE", 0) : Choice(pick.Choice, pick.Confidence)));
            }
            if (questions.ContainsKey("surface"))
            {
                SurfaceCalls++;
                var surface = c.RecordedScope switch
                {
                    { Kind: ExecutionScopeKind.Browser, BrowserKind: BrowserScopeKind.ActiveTab } => ContextualSurface.ActiveBrowserTab,
                    { Kind: ExecutionScopeKind.Browser, BrowserKind: BrowserScopeKind.NewTaskTab } => ContextualSurface.NewBrowserTaskTab,
                    { Kind: ExecutionScopeKind.NativeInteraction } => ContextualSurface.ForegroundNativeWindow,
                    _ => ContextualSurface.Clarify
                };
                return Task.FromResult(Answer("surface", Choice(surface.ToString(), .95)));
            }
            if (questions.ContainsKey("app"))
            {
                AppCalls++;
                return Task.FromResult(Answer("app", Choice("none", .95)));
            }
            throw new InvalidOperationException("Replay never routes: route heads are recorded.");
        }
    }

    private static Replayed Case(string scenarioId, int turn = 0)
        => All.Value.Single(r => r.Case.ScenarioId == scenarioId && r.Case.Turn == turn);

    private static BrowserTabInfo? ActiveTab(ReplayCase c)
    {
        var active = c.Tabs.Where(t => t.Active && t.Origin is not null).ToArray();
        return active.Length == 1 ? new(active[0].TabId, active[0].WindowId, true, active[0].Url, active[0].Title,
            active[0].Provenance, active[0].SessionId, active[0].LastUsedSequence) : null;
    }

    private static string Describe(ExecutionScopeDecision s)
        => s.Browser is { } b ? $"{s.Kind}/{b.Kind} tab={b.TabId} refuted={b.TabClaimRefuted} surfaceOnly={b.IsSurfaceOnly}"
            : $"{s.Kind} {s.Detail}";

    private static string Describe(ScopeInfo? s) => s is null ? "(none)" : $"{s.Kind}/{s.BrowserKind} tab={s.TabId}";

    [Fact]
    public void ReplayTable()
    {
        foreach (var r in All.Value)
            output.WriteLine($"{r.Case.Key,-44} recorded={Describe(r.Case.RecordedScope),-40} replayed={Describe(r.Scope)} tab_calls={r.TabCalls} surface_calls={r.SurfaceCalls} pick={r.Case.RecordedNamedTabPick}");
        Assert.Equal(78, All.Value.Count);
    }

    // ── 1. A content target misclassified as a named tab is refuted by the inventory ──

    [Theory]
    [InlineData("page.product-listing-by-title")]   // "Click on Sharp Objects." → NONE
    [InlineData("tabs.result-from-vendor")]         // Keychron result → NONE at 0.69
    public void ContentTargetNamedTabClaimIsRefutedToTheCurrentPage(string scenarioId)
    {
        var r = All.Value.Single(x => x.Case.ScenarioId == scenarioId && x.Case.NamedTabClaim);
        Assert.Equal("NONE", r.Case.RecordedNamedTabPick?.Choice);
        Assert.Equal(ExecutionScopeKind.Clarify, r.Case.RecordedScope?.Kind);   // before F1
        var browser = Assert.IsType<BrowserExecutionScope>(r.Scope.Browser);
        Assert.Equal(BrowserScopeKind.ActiveTab, browser.Kind);
        Assert.Equal(ActiveTab(r.Case)!.TabId, browser.TabId);
        Assert.True(browser.TabClaimRefuted);
        // The recorded heads say SurfaceReady/SurfaceOnly for "Click on Sharp Objects": it must still act.
        Assert.False(browser.IsSurfaceOnly);
        Assert.False(browser.FocusOnly);
        Assert.Equal(1, r.TabCalls);
        Assert.Equal(0, r.SurfaceCalls);
    }

    [Fact]
    public void HiddenMisclassificationSelectingTheActiveTabRunsOnTheCurrentSurface()
    {
        // "Open the article on cephalopods." — claimed a named tab; the picker chose the active tab.
        var r = Case("page.wikipedia-inline-link");
        Assert.True(r.Case.NamedTabClaim);
        var browser = Assert.IsType<BrowserExecutionScope>(r.Scope.Browser);
        Assert.Equal(BrowserScopeKind.ActiveTab, browser.Kind);
        Assert.Equal(ActiveTab(r.Case)!.TabId, browser.TabId);
        Assert.False(browser.IsSurfaceOnly);
        Assert.False(browser.TabClaimRefuted);
    }

    // ── 2. Real named-tab requests still select tabs ──

    [Fact]
    public void RealNamedTabRequestStillSwitchesTabs()
    {
        var r = All.Value.Single(x => x.Case.ScenarioId == "tabs.switch-named-docs-tab" && x.Case.NamedTabClaim);
        var browser = Assert.IsType<BrowserExecutionScope>(r.Scope.Browser);
        Assert.Equal(BrowserScopeKind.ExistingNamedTab, browser.Kind);
        Assert.Equal(r.Case.RecordedScope?.TabId, browser.TabId);
        Assert.False(r.Case.Tabs.Single(t => t.TabId == browser.TabId).Active);
        Assert.True(browser.ExplicitSelection);
    }

    [Theory]
    [InlineData("tabs.visible-tab-beats-old-task")]
    [InlineData("tabs.return-to-earlier-task")]
    public void AmbiguousNamedTabStillClarifies(string scenarioId)
    {
        foreach (var r in All.Value.Where(x => x.Case.ScenarioId == scenarioId && x.Case.NamedTabClaim
            && x.Case.RecordedNamedTabPick?.Choice == "AMBIGUOUS"))
            Assert.Equal(ExecutionScopeKind.Clarify, r.Scope.Kind);
    }

    // ── 3. Self-contained new tasks are never captured by the active browser context ──

    [Theory]
    [InlineData("dest.hacker-news")]
    [InlineData("dest.university-site")]
    public void SelfContainedDestinationsStayInANewTaskTab(string scenarioId)
    {
        var r = Case(scenarioId);
        Assert.Equal(ContextDependency.SelfContained, r.Case.Route.ContextDependency);
        Assert.Equal(BrowserScopeKind.NewTaskTab, r.Scope.Browser?.Kind);
        Assert.Null(r.Scope.Browser?.TabId);
        Assert.Equal(0, r.TabCalls + r.SurfaceCalls);
    }

    [Fact]
    public void NoSelfContainedRouteLandsInAnExistingTab()
    {
        foreach (var r in All.Value.Where(x => x.Case.Route.ContextDependency == ContextDependency.SelfContained
            && x.Case.Route.TabDisposition is not (TabDisposition.CurrentTab or TabDisposition.ExistingNamedTab)))
            Assert.True(r.Scope.Browser?.Kind is null or BrowserScopeKind.NewTaskTab or BrowserScopeKind.RecentOwnedTaskTab,
                $"{r.Case.Key}: {Describe(r.Scope)}");
    }

    [Fact]
    public void ExplicitCurrentAndNewTabControlsAreUnchanged()
    {
        var explicitTabs = All.Value.Where(x => x.Case.Route.TabDisposition is TabDisposition.CurrentTab or TabDisposition.NewTab).ToArray();
        Assert.NotEmpty(explicitTabs);
        foreach (var r in explicitTabs)
        {
            Assert.Equal(0, r.TabCalls);
            Assert.Equal(Recorded(r.Case.RecordedScope), Recorded(r.Scope));
        }
    }

    // ── 4. Current-surface fallback only with corroborating state ──

    [Fact]
    public void RefutedClaimsReachTheCurrentPageOnlyWithCorroboratingState()
    {
        var refuted = All.Value.Where(r => r.Scope.Browser?.TabClaimRefuted == true).ToArray();
        Assert.NotEmpty(refuted);
        foreach (var r in refuted)
        {
            Assert.True(r.Case.NamedTabClaim, r.Case.Key);
            Assert.Equal("NONE", r.Case.RecordedNamedTabPick?.Choice);
            Assert.Equal(ContextDependency.RequiresCurrentSurface, r.Case.Route.ContextDependency);
            Assert.Equal("chrome", r.Case.Foreground?.ProcessName);
            Assert.Equal(ActiveTab(r.Case)?.TabId, r.Scope.Browser!.TabId);
            Assert.False(r.Scope.Browser.IsSurfaceOnly);
        }
    }

    [Fact]
    public void NamedTabClaimsNeverRunOnAnInferredPageWithoutAMatchOrRefutation()
    {
        foreach (var r in All.Value.Where(x => x.Case.NamedTabClaim && x.Scope.Browser is not null))
            Assert.True(r.Scope.Browser!.ExplicitSelection || r.Scope.Browser.TabClaimRefuted, r.Case.Key);
    }

    // ── 5. F1 changes nothing outside named-tab claims ──

    /// <summary>Native scopes chosen from the installed-app list cannot be replayed: snapshots do
    /// not record installed apps. (The stale Sheets shortcut behind this one is rejected by F3.)</summary>
    private static bool NeedsUnrecordedInstalledApps(ReplayCase c) => c.RecordedScope?.Kind == ExecutionScopeKind.NativeInteraction;

    [Fact]
    public void EveryTurnWithoutANamedTabClaimReplaysItsRecordedScope()
    {
        Assert.Equal(["dest.installed-web-app-sheets#0"],
            All.Value.Where(r => NeedsUnrecordedInstalledApps(r.Case)).Select(r => r.Case.Key));
        var mismatches = All.Value.Where(r => !r.Case.NamedTabClaim && !NeedsUnrecordedInstalledApps(r.Case))
            // A recorded Clarify that named nothing the user could answer is now a specific failure (Unresolved).
            .Where(r => Recorded(r.Case.RecordedScope) != Recorded(r.Scope)
                && !(r.Case.RecordedScope?.Kind == ExecutionScopeKind.Clarify && r.Scope.Kind == ExecutionScopeKind.Unresolved))
            .Select(r => $"{r.Case.Key}: recorded {Recorded(r.Case.RecordedScope)} replayed {Recorded(r.Scope)}")
            .ToArray();
        Assert.True(mismatches.Length == 0, string.Join(Environment.NewLine, mismatches));
        Assert.True(All.Value.Count(r => !r.Case.NamedTabClaim) > 60);
    }

    [Fact]
    public void DirectAndMediaRoutesNeverConsultBrowserContext()
    {
        var direct = All.Value.Where(r => r.Case.Route.Route == CommandRoute.DirectCapability
            || r.Case.Route.MediaRequestKind == MediaRequestKind.Transport && r.Case.Route.MediaOperation is not null).ToArray();
        Assert.True(direct.Length >= 15);
        foreach (var r in direct)
        {
            Assert.Equal(ExecutionScopeKind.DirectCapability, r.Scope.Kind);
            Assert.Equal(0, r.TabCalls + r.SurfaceCalls + r.AppCalls);
        }
    }

    private static string Recorded(ScopeInfo? s) => s is null ? "(none)" : $"{s.Kind}/{s.BrowserKind}/{s.TabId}";

    private static string Recorded(ExecutionScopeDecision s)
        => $"{s.Kind}/{s.Browser?.Kind.ToString() ?? ""}/{s.Browser?.TabId}";
}
