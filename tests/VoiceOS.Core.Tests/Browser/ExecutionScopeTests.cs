using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ExecutionScopeTests
{
    private static readonly WindowCandidate Chrome = new("chrome", "chrome", "Chrome", true, 42);
    private static readonly WindowCandidate Code = new("code", "Code", "Visual Studio Code", true, 75);

    private static BrowserTabInfo Tab(int id, string url, bool active = false,
        BrowserTabProvenance provenance = BrowserTabProvenance.User, long? use = null,
        string? title = null)
        => new(id, 1, active, url, title ?? url, provenance,
            provenance == BrowserTabProvenance.VoiceOs ? $"task-{id}" : null, use);

    private static async Task<(CommandRouteDecision Route, ExecutionScopeDecision Scope, int ContextCalls)> Run(
        string utterance, string route = "COMPUTER_USE", string destination = "None",
        string disposition = "Unspecified", string completeness = "Actionable",
        string media = "None", string mediaOp = "Pause", BrowserTabInfo[]? tabs = null,
        WindowCandidate? foreground = null, ContextualSurface contextChoice = ContextualSurface.NewBrowserTaskTab,
        bool connected = true, string preference = "Unspecified", string endState = "OtherBoundedGoal",
        string relation = "NewTask", string goalShape = "ActionOnSurface",
        AppCandidate[]? apps = null, RecentTaskFrame? recent = null,
        string contextDependency = "SelfContained")
    {
        var gateway = new ScriptedGateway(route, destination, disposition, completeness, media, mediaOp,
            contextChoice, preference, endState, relation, goalShape, contextDependency);
        var router = new TypeSafeCommandRouter(gateway);
        var typed = await router.RouteAsync(utterance);
        var window = foreground ?? Chrome;
        var context = new ExecutionContextSnapshot(window, [window], connected, tabs ?? [], recent, apps);
        var scope = await new ScopeResolver().ResolveAsync(utterance, typed, context, router);
        return (typed, scope, gateway.ContextCalls);
    }

    [Theory]
    [InlineData("maximize Chrome", "None")]
    [InlineData("increase volume", "None")]
    [InlineData("move Chrome to the left monitor", "None")]
    public async Task DirectCommandsSkipContext(string utterance, string destination)
    {
        var result = await Run(utterance, route: "DIRECT_CAPABILITY", destination: destination, connected: false);
        Assert.Equal(ExecutionScopeKind.DirectCapability, result.Scope.Kind);
        Assert.Equal(0, result.ContextCalls);
    }

    [Theory]
    [InlineData("put the browser window on the other display", "None", "Browser", "Unspecified")]
    [InlineData("minimize the Spotify app", "Spotify", "Native", "Unspecified")]
    [InlineData("make this Chrome window larger", "None", "Browser", "CurrentTab")]
    public async Task CompleteDirectRouteCannotBeStolenBySurfaceHeads(string utterance,
        string destination, string preference, string disposition)
    {
        var result = await Run(utterance, route: "DIRECT_CAPABILITY", destination: destination,
            preference: preference, disposition: disposition, connected: true,
            tabs: [Tab(1, "https://example.org/", true)]);
        Assert.Equal(ExecutionScopeKind.DirectCapability, result.Scope.Kind);
        Assert.Equal(0, result.ContextCalls);
    }

    [Fact]
    public async Task PauseIsDirectButExplicitYouTubePauseIsBrowser()
    {
        var direct = await Run("pause", route: "CLARIFY", media: "Transport");
        Assert.Equal(ExecutionScopeKind.DirectCapability, direct.Scope.Kind);
        Assert.Equal(MediaOperation.Pause, direct.Route.MediaOperation);
        var scoped = await Run("pause on YouTube", route: "DIRECT_CAPABILITY",
            destination: "YouTube", media: "Transport");
        Assert.Equal(ExecutionScopeKind.Browser, scoped.Scope.Kind);
        Assert.Null(scoped.Route.MediaOperation);
        Assert.Equal("https://www.youtube.com/", scoped.Scope.Browser?.Destination?.AbsoluteUri);
        Assert.Equal(0, scoped.ContextCalls);
    }

    [Theory]
    [InlineData("play Kendrick Lamar on YouTube", "YouTube", "https://www.youtube.com/")]
    [InlineData("search Kendrick Lamar on Google", "Google", "https://www.google.com/")]
    [InlineData("open Spotify web player", "Spotify", "https://open.spotify.com/")]
    public async Task ExplicitDestinationSkipsContext(string utterance, string name, string url)
    {
        var result = await Run(utterance, destination: name,
            tabs: [Tab(1, "https://www.youtube.com/", true)]);
        Assert.Equal(name, result.Route.DestinationName);
        Assert.Equal(ExecutionScopeKind.Browser, result.Scope.Kind);
        Assert.Equal(0, result.ContextCalls);
        Assert.Equal(name, result.Scope.Browser?.NamedServiceHint);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
        Assert.Equal(url, result.Scope.Browser?.Destination?.AbsoluteUri);
    }

    [Theory]
    [InlineData("open YouTube in a new tab", "YouTube")]
    [InlineData("search React in a new tab", "None")]
    [InlineData("new tab and search React", "None")]
    public async Task NewTabComposesWithDestinationAndGoal(string utterance, string destination)
    {
        var result = await Run(utterance, destination: destination, disposition: "NewTab",
            tabs: [Tab(1, "https://www.youtube.com/", true)]);
        Assert.Equal(TabDisposition.NewTab, result.Route.TabDisposition);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
        Assert.Equal(0, result.ContextCalls);
        if (destination == "YouTube")
            Assert.Equal("https://www.youtube.com/", result.Scope.Browser?.Destination?.AbsoluteUri);
        else
            Assert.Null(result.Scope.Browser?.Destination);
    }

    [Fact]
    public async Task ExplicitUrlPrecedesIncidentalService()
    {
        var result = await Run("open https://example.org/docs on YouTube",
            destination: "YouTube", disposition: "NewTab");
        Assert.Equal(SemanticDestinationKind.ExplicitUrl, result.Route.DestinationKind);
        Assert.Equal("https://example.org/docs", result.Scope.Browser?.Destination?.AbsoluteUri);
        Assert.Null(result.Scope.Browser?.NamedServiceHint);
        Assert.Equal(0, result.ContextCalls);
    }

    [Fact]
    public async Task ExplicitNamedTabResolvesOneAndClarifiesMany()
    {
        const string utterance = "use the YouTube tab I already have open";
        var one = await Run(utterance, destination: "YouTube", disposition: "ExistingNamedTab",
            tabs: [Tab(1, "https://news.example/", true), Tab(2, "https://www.youtube.com/")]);
        Assert.Equal(BrowserScopeKind.ExistingNamedTab, one.Scope.Browser?.Kind);
        Assert.Equal(2, one.Scope.Browser?.TabId);
        Assert.False(one.Scope.Browser?.FocusOnly);
        var many = await Run(utterance, destination: "YouTube", disposition: "ExistingNamedTab",
            tabs: [Tab(1, "https://www.youtube.com/", true), Tab(2, "https://www.youtube.com/watch?v=1")]);
        Assert.Equal(ExecutionScopeKind.Clarify, many.Scope.Kind);
        Assert.Equal(0, many.ContextCalls);
    }

    [Theory]
    [InlineData("search Kendrick Lamar", "https://www.youtube.com/", ContextualSurface.ActiveBrowserTab, BrowserScopeKind.ActiveTab)]
    [InlineData("search mechanical keyboards", "https://www.amazon.com/", ContextualSurface.ActiveBrowserTab, BrowserScopeKind.ActiveTab)]
    [InlineData("look up the tour dates", "https://www.google.com/", ContextualSurface.ActiveBrowserTab, BrowserScopeKind.ActiveTab)]
    [InlineData("search Kendrick Lamar", "https://news.example/", ContextualSurface.NewBrowserTaskTab, BrowserScopeKind.NewTaskTab)]
    [InlineData("change it to one way", "https://www.google.com/travel/flights", ContextualSurface.ActiveBrowserTab, BrowserScopeKind.ActiveTab)]
    public async Task ContextChoosesAmongOfferedSurfaces(string utterance, string url,
        ContextualSurface choice, BrowserScopeKind expected)
    {
        var result = await Run(utterance, tabs: [Tab(1, url, true)], contextChoice: choice,
            relation: "Uncertain", contextDependency: "Uncertain");
        Assert.Equal(expected, result.Scope.Browser?.Kind);
        Assert.Equal(1, result.ContextCalls);
    }

    [Fact]
    public async Task NativeForegroundCanWinContextWithoutImplementingUia()
    {
        var result = await Run("click Source Control", route: "NATIVE_INTERACTION",
            foreground: Code, contextChoice: ContextualSurface.ForegroundNativeWindow);
        Assert.Equal(ExecutionScopeKind.NativeInteraction, result.Scope.Kind);
        Assert.Equal((nint)75, result.Scope.Native?.WindowHandle);
        Assert.Equal(1, result.ContextCalls);
    }

    [Fact]
    public async Task PrematureNativeRouteCanBeRepairedByContext()
    {
        var result = await Run("search for TypeSafe", route: "NATIVE_INTERACTION",
            foreground: Code, contextChoice: ContextualSurface.NewBrowserTaskTab);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
        Assert.Equal(1, result.ContextCalls);
    }

    [Theory]
    [InlineData("Kendrick Lamar")]
    [InlineData("YouTube")]
    [InlineData("React")]
    public async Task BareEntitiesNeverReachContext(string utterance)
    {
        var result = await Run(utterance, completeness: "BareEntity");
        Assert.Equal(ExecutionScopeKind.Clarify, result.Scope.Kind);
        Assert.Equal(0, result.ContextCalls);
    }

    [Fact]
    public async Task ImplicitSelectionNeverHijacksInactiveUserTab()
    {
        var result = await Run("search for a tutorial", tabs:
            [Tab(1, "https://news.example/", true), Tab(2, "https://www.youtube.com/")],
            contextChoice: ContextualSurface.NewBrowserTaskTab);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
    }

    [Fact]
    public async Task OwnedReuseNeedsActualRecency()
    {
        var result = await Run("play Kendrick Lamar on YouTube", destination: "YouTube",
            tabs: [Tab(1, "https://news.example/", true),
                Tab(99, "https://www.youtube.com/", provenance: BrowserTabProvenance.VoiceOs, use: 1),
                Tab(2, "https://www.youtube.com/", provenance: BrowserTabProvenance.VoiceOs, use: 7)]);
        Assert.Equal(2, result.Scope.Browser?.TabId);
        var unknown = await Run("play Kendrick Lamar on YouTube", destination: "YouTube",
            tabs: [Tab(1, "https://news.example/", true),
                Tab(99, "https://www.youtube.com/", provenance: BrowserTabProvenance.VoiceOs)]);
        Assert.Equal(BrowserScopeKind.NewTaskTab, unknown.Scope.Browser?.Kind);
    }

    [Fact]
    public async Task ChromeMustBeDesktopForegroundForActiveTab()
    {
        var result = await Run("search React", tabs: [Tab(1, "https://www.google.com/", true)],
            foreground: Code, contextChoice: ContextualSurface.ActiveBrowserTab,
            relation: "Uncertain", contextDependency: "Uncertain");
        Assert.Equal(ExecutionScopeKind.Clarify, result.Scope.Kind);
    }

    [Theory]
    [InlineData("Native", ExecutionScopeKind.NativeInteraction)]
    [InlineData("Browser", ExecutionScopeKind.Browser)]
    [InlineData("Unspecified", ExecutionScopeKind.NativeInteraction)]
    public async Task ExplicitPreferenceAndInstalledDefault(string preference, ExecutionScopeKind expected)
    {
        var result = await Run("bring up the service and show its catalog", destination: "Spotify",
            preference: preference, apps: [new("spotify-app", "Spotify", "spotify")]);
        Assert.Equal(expected, result.Scope.Kind);
        if (expected == ExecutionScopeKind.NativeInteraction)
            Assert.Equal("spotify-app", result.Scope.Native?.AppCandidateId);
    }

    [Fact]
    public async Task MissingNativeFallsBackToBrowserUnlessNativeWasExplicit()
    {
        var fallback = await Run("show the service", destination: "Spotify", apps: []);
        Assert.Equal(ExecutionScopeKind.Browser, fallback.Scope.Kind);
        var explicitNative = await Run("show the desktop app", destination: "Spotify",
            preference: "Native", apps: []);
        Assert.Equal(ExecutionScopeKind.Clarify, explicitNative.Scope.Kind);
    }

    [Fact]
    public async Task SurfaceSelectionOnlyCompletesButAttachedActionRemains()
    {
        var tab = Tab(8, "https://github.com/", title: "Project board");
        var focus = await Run("bring the existing tab forward", destination: "GitHub",
            disposition: "ExistingNamedTab", endState: "SurfaceReady", goalShape: "SurfaceOnly",
            tabs: [tab]);
        var action = await Run("use the existing tab and update the project", destination: "GitHub",
            disposition: "ExistingNamedTab", endState: "StateChanged", tabs: [tab]);
        Assert.True(focus.Scope.Browser?.FocusOnly);
        Assert.False(action.Scope.Browser?.FocusOnly);
    }

    [Fact]
    public async Task PriorTaskRelationCannotSelectStoredBrowserSurface()
    {
        var recentTab = Tab(7, "https://example.org/work", provenance: BrowserTabProvenance.VoiceOs, use: 4);
        var frame = new RecentTaskFrame(7, "task-7", "https://example.org/work",
            "edit prior value", VoiceOS.Core.Interaction.InteractionCompletionState.Complete, DateTimeOffset.UtcNow);
        var continued = await Run("make the earlier change more precise", relation: "ContinueRecent",
            contextDependency: "Uncertain",
            tabs: [Tab(1, "https://news.example/", true), recentTab], recent: frame);
        Assert.Equal(ExecutionScopeKind.Clarify, continued.Scope.Kind);
        Assert.Equal(0, continued.ContextCalls);
        var required = await Run("Go back to those results.", relation: "RequiresRecent",
            contextDependency: "Uncertain",
            tabs: [Tab(1, "https://news.example/", true), recentTab], recent: frame);
        Assert.Equal(ExecutionScopeKind.Clarify, required.Scope.Kind);
        Assert.Equal(0, required.ContextCalls);
        var independent = await Run("Search for React.", relation: "ContinueRecent",
            contextDependency: "SelfContained",
            tabs: [Tab(1, "https://news.example/", true), recentTab], recent: frame);
        Assert.Equal(BrowserScopeKind.NewTaskTab, independent.Scope.Browser?.Kind);
    }

    [Fact]
    public async Task StaleRecentCandidateCannotBlockFreshOrExplicitScope()
    {
        var frame = new RecentTaskFrame(7, "task-7", "https://old.example/", "prior goal",
            VoiceOS.Core.Interaction.InteractionCompletionState.Complete, DateTimeOffset.UtcNow);
        var tabs = new[] { Tab(1, "https://current.example/", true) };
        var fresh = await Run("locate a useful article", tabs: tabs, recent: frame);
        Assert.Equal(BrowserScopeKind.NewTaskTab, fresh.Scope.Browser?.Kind);
        var candidate = await Run("continue with another article", relation: "ContinueRecent",
            tabs: tabs, recent: frame);
        Assert.Equal(BrowserScopeKind.NewTaskTab, candidate.Scope.Browser?.Kind);
        var explicitDestination = await Run("browse the service", destination: "GitHub",
            relation: "RequiresRecent", tabs: tabs, recent: frame);
        Assert.Equal(BrowserScopeKind.NewTaskTab, explicitDestination.Scope.Browser?.Kind);
        var current = await Run("use this page", disposition: "CurrentTab",
            relation: "RequiresRecent", tabs: tabs, recent: frame);
        Assert.Equal(BrowserScopeKind.ActiveTab, current.Scope.Browser?.Kind);
    }

    [Theory]
    [InlineData("locate an overview of the topic")]
    [InlineData("get me a list of relevant resources")]
    [InlineData("show available examples")]
    public async Task FreshBrowserGoalPreservesUnrelatedForeground(string utterance)
    {
        var result = await Run(utterance, tabs: [Tab(1, "https://unrelated.example/", true)],
            contextChoice: ContextualSurface.Clarify);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
        Assert.Equal(0, result.ContextCalls);
    }

    [Fact]
    public async Task BareNewTabHasTypedSurfaceGoal()
    {
        var result = await Run("please make another tab", disposition: "NewTab",
            endState: "SurfaceReady", goalShape: "SurfaceOnly", connected: false);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
        Assert.Equal(SemanticEndState.SurfaceReady, result.Scope.Browser?.EndState);
    }

    [Fact]
    public async Task ExplicitCurrentTabOverridesFreshTaskPreference()
    {
        var result = await Run("change the choice on this tab", disposition: "CurrentTab",
            tabs: [Tab(3, "https://unrelated.example/", true)]);
        Assert.Equal(3, result.Scope.Browser?.TabId);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(0, result.ContextCalls);
    }

    [Theory]
    [InlineData("pick the third result from the current page")]
    [InlineData("select result three in the active tab")]
    [InlineData("choose the third item on this screen")]
    [InlineData("open the third result in the surface I'm viewing")]
    [InlineData("from the page in front of me, select the third result")]
    public async Task ExplicitCurrentSurfaceHeadPrecedesFreshTaskFallback(string utterance)
    {
        var result = await Run(utterance, disposition: "CurrentTab", relation: "NewTask",
            tabs: [Tab(3, "https://www.google.com/search?q=example", true)]);
        Assert.Equal(TabDisposition.CurrentTab, result.Route.TabDisposition);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
        Assert.Equal(0, result.ContextCalls);
    }

    [Fact]
    public async Task ExplicitCurrentTabCanPrepareKnownBackgroundChromeTab()
    {
        var result = await Run("choose a result on the page in front of me",
            disposition: "CurrentTab", tabs: [Tab(3, "https://example.org/", true)],
            foreground: Code);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
        Assert.False(result.Scope.Browser?.RequireForegroundChrome);
    }

    [Fact]
    public async Task ExplicitCurrentTabWithoutKnownActiveBrowserTabClarifies()
    {
        var result = await Run("use the current browser tab", disposition: "CurrentTab",
            tabs: [Tab(3, "https://example.org/")], foreground: Code);
        Assert.Equal(ExecutionScopeKind.Clarify, result.Scope.Kind);
    }

    [Theory]
    [InlineData("Pick the third result.")]
    [InlineData("Open the second result.")]
    [InlineData("Read the second result.")]
    [InlineData("Click the second result.")]
    [InlineData("Click Explore.")]
    [InlineData("Choose the second option.")]
    [InlineData("Select the blue button.")]
    [InlineData("Open the first post.")]
    [InlineData("Scroll down.")]
    public async Task ImplicitVisibleTargetUsesCurrentSurfaceDependency(string utterance)
    {
        var result = await Run(utterance, contextDependency: "RequiresCurrentSurface",
            tabs: [Tab(3, "https://example.org/results", true)]);
        Assert.Equal(TabDisposition.Unspecified, result.Route.TabDisposition);
        Assert.Equal(ContextDependency.RequiresCurrentSurface, result.Route.ContextDependency);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
        Assert.Equal(0, result.ContextCalls);
    }

    [Theory]
    [InlineData("Pick the third result.", "RequiresRecent")]
    [InlineData("Click the second result.", "ContinueRecent")]
    public async Task CurrentSurfaceOutranksRecentTaskRelation(string utterance, string relation)
    {
        var prior = Tab(7, "https://old.example/results", provenance: BrowserTabProvenance.VoiceOs);
        var frame = new RecentTaskFrame(7, "task-7", prior.Url!, "earlier search",
            VoiceOS.Core.Interaction.InteractionCompletionState.Complete, DateTimeOffset.UtcNow);
        var result = await Run(utterance, relation: relation,
            contextDependency: "RequiresCurrentSurface",
            tabs: [Tab(3, "https://current.example/results", true), prior], recent: frame);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
    }

    [Fact]
    public async Task CurrentSurfaceDoesNotRequireUnavailableRecentTask()
    {
        var result = await Run("Pick the third result.", relation: "RequiresRecent",
            contextDependency: "RequiresCurrentSurface",
            tabs: [Tab(3, "https://current.example/results", true)]);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
    }

    [Fact]
    public async Task ImplicitVisibleTargetInBackgroundChromeUsesKnownActiveTab()
    {
        var result = await Run("Pick the third result.",
            contextDependency: "RequiresCurrentSurface", foreground: Code,
            tabs: [Tab(3, "https://example.org/results", true)]);
        Assert.Equal(BrowserScopeKind.ActiveTab, result.Scope.Browser?.Kind);
        Assert.Equal(3, result.Scope.Browser?.TabId);
        Assert.False(result.Scope.Browser?.RequireForegroundChrome);
    }

    [Fact]
    public async Task ImplicitVisibleTargetWithMultipleActiveBrowserTabsClarifies()
    {
        var result = await Run("Pick the third result.",
            contextDependency: "RequiresCurrentSurface",
            tabs: [Tab(3, "https://example.org/results", true),
                Tab(4, "https://example.net/results", true)]);
        Assert.Equal(ExecutionScopeKind.Clarify, result.Scope.Kind);
    }

    [Fact]
    public async Task RequiresRecentWithoutCurrentSurfaceClarifiesEvenWithValidFrame()
    {
        var tab = Tab(7, "https://example.org/results", provenance: BrowserTabProvenance.VoiceOs);
        var frame = new RecentTaskFrame(7, "task-7", tab.Url!, "show results",
            VoiceOS.Core.Interaction.InteractionCompletionState.Complete, DateTimeOffset.UtcNow);
        var valid = await Run("Go back to those results.",
            relation: "RequiresRecent", contextDependency: "Uncertain", tabs: [tab], recent: frame);
        Assert.Equal(ExecutionScopeKind.Clarify, valid.Scope.Kind);
        Assert.Null(valid.Scope.Browser);
        Assert.Equal(0, valid.ContextCalls);
        var missing = await Run("Go back to those results.",
            relation: "RequiresRecent", contextDependency: "Uncertain", tabs: [tab]);
        Assert.Equal(ExecutionScopeKind.Clarify, missing.Scope.Kind);
        Assert.Null(missing.Scope.Browser);
    }

    [Theory]
    [InlineData("Open Spotify on the web", "Spotify", "Unspecified", "SelfContained", BrowserScopeKind.NewTaskTab)]
    [InlineData("Open a new tab and search for React", "None", "NewTab", "RequiresCurrentSurface", BrowserScopeKind.NewTaskTab)]
    [InlineData("Search for React", "None", "Unspecified", "SelfContained", BrowserScopeKind.NewTaskTab)]
    [InlineData("Open the third Google result for React", "Google", "Unspecified", "SelfContained", BrowserScopeKind.NewTaskTab)]
    public async Task SelfContainedAndExplicitNewTabKeepFreshScope(string utterance,
        string destination, string disposition, string dependency, BrowserScopeKind expected)
    {
        var result = await Run(utterance, destination: destination, disposition: disposition,
            contextDependency: dependency, preference: destination == "Spotify" ? "Browser" : "Unspecified",
            tabs: [Tab(3, "https://example.org/", true)]);
        Assert.Equal(expected, result.Scope.Browser?.Kind);
    }

    [Theory]
    [InlineData("Open the Spotify tab", "SurfaceReady", "SurfaceOnly", true)]
    [InlineData("Use the Spotify tab and search for Chase Atlantic", "ResultsVisible", "ActionOnSurface", false)]
    public async Task NamedTabWinsOverContextDependency(string utterance,
        string endState, string goalShape, bool focusOnly)
    {
        var result = await Run(utterance, destination: "Spotify", disposition: "ExistingNamedTab",
            contextDependency: "RequiresCurrentSurface", endState: endState,
            goalShape: goalShape, tabs: [Tab(3, "https://example.org/", true),
                Tab(8, "https://open.spotify.com/", title: "Spotify")]);
        Assert.Equal(BrowserScopeKind.ExistingNamedTab, result.Scope.Browser?.Kind);
        Assert.Equal(8, result.Scope.Browser?.TabId);
        Assert.Equal(focusOnly, result.Scope.Browser?.FocusOnly);
    }

    [Fact]
    public async Task ExplicitDestinationWinsOverCurrentSurfaceDependency()
    {
        var result = await Run("Open Spotify on the web", destination: "Spotify",
            preference: "Browser", contextDependency: "RequiresCurrentSurface",
            tabs: [Tab(3, "https://example.org/", true)]);
        Assert.Equal(BrowserScopeKind.NewTaskTab, result.Scope.Browser?.Kind);
    }

    [Fact]
    public async Task DirectCapabilityWinsOverCurrentSurfaceDependency()
    {
        var result = await Run("maximize Chrome", route: "DIRECT_CAPABILITY",
            contextDependency: "RequiresCurrentSurface", tabs: [Tab(3, "https://example.org/", true)]);
        Assert.Equal(ExecutionScopeKind.DirectCapability, result.Scope.Kind);
    }

    private sealed class ScriptedGateway(string route, string destination, string disposition,
        string completeness, string media, string mediaOp, ContextualSurface contextChoice,
        string preference, string endState, string relation, string goalShape,
        string contextDependency) : IJevGateway
    {
        public int ContextCalls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions,
            CancellationToken cancellationToken = default)
        {
            JevAnswer Choice(string value) => new("choice", value, new Dictionary<string, double>(), .98);
            if (questions.ContainsKey("surface"))
            {
                ContextCalls++;
                return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(
                    new Dictionary<string, JevAnswer> { ["surface"] = Choice(contextChoice.ToString()) });
            }
            if (questions.ContainsKey("tab"))
            {
                var candidates = questions["tab"].Criteria!;
                var matching = candidates.Where(pair => pair.Key.StartsWith("tab_")
                    && pair.Value.Contains($"service={destination}", StringComparison.Ordinal)).ToArray();
                return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(
                    new Dictionary<string, JevAnswer>
                    {
                        ["tab"] = Choice(matching.Length == 1 ? matching[0].Key : "AMBIGUOUS")
                    });
            }
            Assert.Contains("destination", questions.Keys);
            Assert.Contains("tab_disposition", questions.Keys);
            Assert.Contains("context_dependency", questions.Keys);
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(
                new Dictionary<string, JevAnswer>
                {
                    ["route"] = Choice(route), ["destination"] = Choice(destination),
                    ["tab_disposition"] = Choice(disposition),
                    ["intent_completeness"] = Choice(completeness),
                    ["media_request_kind"] = Choice(media), ["media_op"] = Choice(mediaOp),
                    ["surface_preference"] = Choice(preference), ["end_state"] = Choice(endState),
                    ["task_relation"] = Choice(relation), ["goal_shape"] = Choice(goalShape),
                    ["context_dependency"] = Choice(contextDependency)
                });
        }
    }
}
