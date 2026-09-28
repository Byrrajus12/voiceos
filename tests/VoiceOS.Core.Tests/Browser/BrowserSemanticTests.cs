using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserSemanticTests
{
    [Fact]
    public void BootstrapUrl_UsesCorroboratedNormalizedServiceUrl()
        => Assert.Equal("https://www.imdb.com/", BrowserGoal.BootstrapUrl(new("Search IMDb for Dune",
            Normalization: FakeNormalizer.Normalized with { PreferredService = "IMDb", PreferredServiceUrl = "https://www.imdb.com/" })));

    [Fact]
    public void BootstrapUrl_RegistryScopedAndExplicitStillWin()
    {
        var goal = new BrowserGoal("search", NamedServiceHint: "YouTube", Normalization:
            FakeNormalizer.Normalized with { PreferredService = "IMDb", PreferredServiceUrl = "https://www.imdb.com/" });
        Assert.Equal("https://www.youtube.com/", BrowserGoal.BootstrapUrl(goal));
        goal = goal with { ScopedDestination = new("https://example.org/") };
        Assert.Equal("https://example.org/", BrowserGoal.BootstrapUrl(goal));
        Assert.Equal("https://example.com/", BrowserGoal.BootstrapUrl(goal with { ExplicitUrl = new("https://example.com/") }));
    }

    [Theory]
    [InlineData("IMDb", "https://evil.example/")]
    [InlineData(null, "https://www.imdb.com/")]
    [InlineData("IMDb", "http://www.imdb.com/")]
    [InlineData("IMDb", "https://www.imdb.com/title/123")]
    [InlineData("IMDb", "https://user@www.imdb.com/")]
    [InlineData("IMDb", "https://www.imdb.com/?q=x")]
    [InlineData("IMDb", "https://www.imdb.com/#x")]
    [InlineData("The New York Times", "https://www.nytimes.com/")]
    public void BootstrapUrl_UncorroboratedOrInvalidUrl_FallsBackToGoogle(string? service, string url)
        => Assert.Equal("https://www.google.com/", BrowserGoal.BootstrapUrl(new("search",
            Normalization: FakeNormalizer.Normalized with { PreferredService = service, PreferredServiceUrl = url })));

    [Fact]
    public async Task SearchIMDb_OpensImdbTaskTab()
    {
        var transport = new StaticTransport();
        var gateway = new FakeGateway((_, _) => Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99))));
        var service = new BrowserInteractionService(transport, gateway, new FakeNormalizer { Result =
            FakeNormalizer.Normalized with { PreferredService = "IMDb", PreferredServiceUrl = "https://www.imdb.com/" } });
        await service.RunAsync("Search IMDb for Dune");
        Assert.Equal("https://www.imdb.com/", transport.LastOpenUrl);
    }

    [Theory]
    [InlineData("play", MediaOperation.Play)]
    [InlineData("play the song", MediaOperation.Play)]
    [InlineData("resume", MediaOperation.Play)]
    [InlineData("resume the song", MediaOperation.Play)]
    [InlineData("pause", MediaOperation.Pause)]
    [InlineData("pause the song", MediaOperation.Pause)]
    [InlineData("pause this", MediaOperation.Pause)]
    [InlineData("stop playing this", MediaOperation.Pause)]
    [InlineData("skip this", MediaOperation.Next)]
    [InlineData("next", MediaOperation.Next)]
    [InlineData("next song", MediaOperation.Next)]
    [InlineData("go to the next song", MediaOperation.Next)]
    [InlineData("previous song", MediaOperation.Previous)]
    public async Task Router_CurrentMediaReferences_UseTypedTransport(string utterance, MediaOperation expected)
    {
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Contains("media_request_kind", questions.Keys);
            Assert.Contains("media_op", questions.Keys);
            return Answers(("route", Choice("CLARIFY", .51)),
                ("intent_completeness", Choice("Actionable", .98)),
                ("media_request_kind", Choice("Transport", .94)),
                ("media_op", Choice(expected.ToString(), .96)),
                ("return_target", Choice(expected == MediaOperation.Previous ? "MediaPlayback" : "None", .9)));
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(CommandRoute.DirectCapability, route.Route);
        Assert.Equal(RoutingReason.MediaTransport, route.Reason);
        Assert.Equal(MediaRequestKind.Transport, route.MediaRequestKind);
        Assert.Equal(expected, route.MediaOperation);
    }

    [Theory]
    [InlineData(MediaOperation.Play)]
    [InlineData(MediaOperation.Pause)]
    [InlineData(MediaOperation.Next)]
    public async Task Router_NonPreviousTransportIsUnaffectedByReturnTargetHead(MediaOperation operation)
    {
        // The Previous gate must not require any new head for other transport operations.
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("CLARIFY", .51)),
            ("intent_completeness", Choice("Actionable", .98)),
            ("media_request_kind", Choice("Transport", .94)),
            ("media_op", Choice(operation.ToString(), .96))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("media transport");
        Assert.Equal(CommandRoute.DirectCapability, route.Route);
        Assert.Equal(operation, route.MediaOperation);
    }

    [Theory]
    [InlineData("RequiresCurrentSurface")]
    [InlineData("SelfContained")]
    public async Task Router_PreviousPredictionWithNavigationBackIsBrowserNotMedia(string contextDependency)
    {
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Contains("return_target", questions.Keys);
            return Answers(("route", Choice("DIRECT_CAPABILITY", .8)),
                ("intent_completeness", Choice("Actionable", .98)),
                ("media_request_kind", Choice("Transport", .9)),
                ("media_op", Choice("Previous", .95)),
                ("return_target", Choice("NavigationHistory", .9)),
                ("context_dependency", Choice(contextDependency, .9)));
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("could you go back?");
        Assert.Equal(CommandRoute.ComputerUse, route.Route);
        Assert.Null(route.MediaOperation);
        Assert.NotEqual(RoutingReason.MediaTransport, route.Reason);
        Assert.Equal(MediaRequestKind.None, route.MediaRequestKind);
        Assert.Equal(ContextDependency.RequiresCurrentSurface, route.ContextDependency);
    }

    [Theory]
    [InlineData("Uncertain")]
    [InlineData("None")]
    [InlineData(null)]
    public async Task Router_PreviousPredictionWithoutAffirmativeMediaClarifies(string? returnTarget)
    {
        var gateway = new FakeGateway((_, _) =>
        {
            var answers = new List<(string, JevAnswer)> { ("route", Choice("DIRECT_CAPABILITY", .8)),
                ("intent_completeness", Choice("Actionable", .98)),
                ("media_request_kind", Choice("Transport", .9)), ("media_op", Choice("Previous", .95)) };
            if (returnTarget is not null) answers.Add(("return_target", Choice(returnTarget, .9)));
            return Answers([.. answers]);
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("go back");
        Assert.Equal(CommandRoute.Clarify, route.Route);
        Assert.Null(route.MediaOperation);
    }

    [Fact]
    public async Task Router_NavigationBackIsNeverADirectCapability()
    {
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("DIRECT_CAPABILITY", .9)),
            ("intent_completeness", Choice("Actionable", .98)),
            ("media_request_kind", Choice("None", .9)),
            ("return_target", Choice("NavigationHistory", .9))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("go back a page");
        Assert.Equal(CommandRoute.ComputerUse, route.Route);
        Assert.Equal(ContextDependency.RequiresCurrentSurface, route.ContextDependency);
    }

    [Theory]
    [InlineData("None", "NamedEntity", CommandRoute.ComputerUse)]
    [InlineData("Instagram", "Uncertain", CommandRoute.ComputerUse)]
    [InlineData("None", "BrowserItself", CommandRoute.DirectCapability)]
    public async Task Router_ExplicitWebPreferenceForNamedEntityIsNotADirectCapability(string destination,
        string entity, CommandRoute expected)
    {
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("DIRECT_CAPABILITY", .9)),
            ("intent_completeness", Choice("Actionable", .98)),
            ("media_request_kind", Choice("None", .9)), ("destination", Choice(destination, .9)),
            ("surface_preference", Choice("Browser", .9)), ("requested_entity", Choice(entity, .9)),
            ("goal_shape", Choice("SurfaceOnly", .9)), ("end_state", Choice("SurfaceReady", .9))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("open it on the web");
        Assert.Equal(expected, route.Route);
    }

    [Theory]
    [InlineData("play Kendrick Lamar")]
    [InlineData("play some Kendrick Lamar")]
    [InlineData("play Runaway")]
    [InlineData("put on Blinding Lights")]
    [InlineData("play Runaway on YouTube")]
    public async Task Router_NewContentTarget_CannotBecomeDirectMedia(string utterance)
    {
        var gateway = new FakeGateway((_, _) => Answers(
            ("route", Choice("DIRECT_CAPABILITY", .97)),
            ("intent_completeness", Choice("Actionable", .98)),
            ("media_request_kind", Choice("ContentSelection", .96)),
            ("media_op", Choice("Play", .99))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(CommandRoute.ComputerUse, route.Route);
        Assert.Equal(MediaRequestKind.ContentSelection, route.MediaRequestKind);
        Assert.Null(route.MediaOperation);
    }

    [Theory]
    [InlineData("Clay.")]
    [InlineData("Kendrick Lamar")]
    [InlineData("Spotify")]
    [InlineData("YouTube")]
    [InlineData("React")]
    public async Task Router_BareEntityClarifiesEvenWhenOtherHeadsSuggestWebTask(string utterance)
    {
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Contains("intent_completeness", questions.Keys);
            return Answers(("route", Choice("COMPUTER_USE", .97)),
                ("intent_completeness", Choice("BareEntity", .95)),
                ("media_request_kind", Choice("ContentSelection", .91)));
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(CommandRoute.Clarify, route.Route);
        Assert.Equal(RoutingReason.IncompleteIntent, route.Reason);
    }

    [Theory]
    [InlineData("search Clay")]
    [InlineData("open Clay")]
    [InlineData("play Kendrick Lamar")]
    [InlineData("put on Blinding Lights")]
    [InlineData("go to YouTube")]
    [InlineData("search React")]
    public async Task Router_ExplicitActionRemainsActionable(string utterance)
    {
        var gateway = new FakeGateway((_, _) => Answers(
            ("route", Choice("COMPUTER_USE", .97)),
            ("intent_completeness", Choice("Actionable", .96)),
            ("media_request_kind", Choice("None", .90))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(CommandRoute.ComputerUse, route.Route);
    }

    [Fact]
    public void BrowserGoal_UsesOnlyTypedDestination()
    {
        var goal = new BrowserGoal("show the product") { ScopedDestination = new("https://example.org/") };
        Assert.Equal("https://example.org/", BrowserGoal.BootstrapUrl(goal));
    }

    [Fact]
    public void BrowserGoal_NormalizedServiceResolvesWithoutTrustingModelUrl()
    {
        var goal = BrowserGoal.FromUtterance("find a restaurant") with
        {
            Normalization = new("find restaurant", "restaurant", "food", "Uber Eats",
                "https://lookalike.example/", ["restaurant"], "results visible", [])
        };
        Assert.Equal("https://www.ubereats.com/", BrowserGoal.BootstrapUrl(goal));
    }

    [Fact]
    public async Task Router_PreservesWholeRequestBeforeDirectExecution()
    {
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("COMPUTER_USE", .97)),
            ("intent_completeness", Choice("Actionable", .96))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("open Chrome and search YouTube for Andrew Huberman");
        Assert.Equal(CommandRoute.ComputerUse, route.Route);
    }

    [Fact]
    public async Task Router_PreservesDirectPhaseOneRoute()
    {
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("DIRECT_CAPABILITY", .98)),
            ("intent_completeness", Choice("Actionable", .96))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("maximize Chrome");
        Assert.Equal(CommandRoute.DirectCapability, route.Route);
    }

    [Fact]
    public async Task Router_EmitsTypedSurfaceAndEndStateInOneCall()
    {
        var calls = 0;
        var gateway = new FakeGateway((_, questions) =>
        {
            calls++;
            Assert.Contains("surface_preference", questions.Keys);
            Assert.Contains("end_state", questions.Keys);
            Assert.Contains("task_relation", questions.Keys);
            Assert.Contains("context_dependency", questions.Keys);
            return Answers(("route", Choice("COMPUTER_USE")),
                ("intent_completeness", Choice("Actionable")),
                ("surface_preference", Choice("Browser")),
                ("end_state", Choice("StateChanged")),
                ("task_relation", Choice("ContinueRecent")),
                ("context_dependency", Choice("Uncertain")));
        });
        var result = await new TypeSafeCommandRouter(gateway).RouteAsync("adjust the earlier choice");
        Assert.Equal(1, calls);
        Assert.Equal(SurfacePreference.Browser, result.SurfacePreference);
        Assert.Equal(SemanticEndState.StateChanged, result.EndState);
        Assert.Equal(TaskRelation.ContinueRecent, result.TaskRelation);
        Assert.Equal(ContextDependency.Uncertain, result.ContextDependency);
    }

    [Theory]
    [InlineData("Search for React", "SelfContained")]
    [InlineData("Open Spotify on the web", "SelfContained")]
    [InlineData("Pick the third result", "RequiresCurrentSurface")]
    [InlineData("Open the second result", "RequiresCurrentSurface")]
    [InlineData("Read the second result", "RequiresCurrentSurface")]
    [InlineData("Click the second result", "RequiresCurrentSurface")]
    [InlineData("Click Explore", "RequiresCurrentSurface")]
    [InlineData("Choose the second option", "RequiresCurrentSurface")]
    [InlineData("Open the first post", "RequiresCurrentSurface")]
    [InlineData("Scroll down", "RequiresCurrentSurface")]
    [InlineData("Select the blue button", "RequiresCurrentSurface")]
    [InlineData("Open the third Google result for React", "SelfContained")]
    public async Task Router_EmitsOrthogonalContextDependencyInExistingCall(
        string utterance, string dependency)
    {
        var calls = 0;
        var gateway = new FakeGateway((_, questions) =>
        {
            calls++;
            Assert.Contains("context_dependency", questions.Keys);
            Assert.Contains("do not choose a tab", questions["context_dependency"].Instructions,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TaskRelation separately classifies continuity",
                questions["context_dependency"].Instructions);
            Assert.Equal(new[] { "SelfContained", "RequiresCurrentSurface", "Uncertain" },
                questions["context_dependency"].Criteria!.Keys);
            return Answers(("route", Choice("COMPUTER_USE")),
                ("intent_completeness", Choice("Actionable")),
                ("media_request_kind", Choice("None")),
                ("tab_disposition", Choice("Unspecified")),
                ("context_dependency", Choice(dependency)));
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(1, calls);
        Assert.Equal(Enum.Parse<ContextDependency>(dependency), route.ContextDependency);
        Assert.Equal(TabDisposition.Unspecified, route.TabDisposition);
    }

    [Theory]
    [InlineData("Go back to those results")]
    [InlineData("Open the one from before")]
    [InlineData("Continue that search")]
    public async Task Router_LeavesPriorTaskContinuityToTaskRelation(string utterance)
    {
        var gateway = new FakeGateway((_, _) =>
            Answers(("route", Choice("COMPUTER_USE")),
                ("intent_completeness", Choice("Actionable")),
                ("task_relation", Choice("RequiresRecent")),
                ("context_dependency", Choice("Uncertain"))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(TaskRelation.RequiresRecent, route.TaskRelation);
        Assert.Equal(ContextDependency.Uncertain, route.ContextDependency);
    }

    [Fact]
    public void ContextDependencyHasOnlyThreeValues()
        => Assert.Equal(new[] { "Uncertain", "SelfContained", "RequiresCurrentSurface" },
            Enum.GetNames<ContextDependency>());

    [Theory]
    [InlineData("Open the Backend UI Parallel Tracks tab", "ExistingNamedTab")]
    [InlineData("Open the React tab", "ExistingNamedTab")]
    [InlineData("Open the Agent Island tab", "ExistingNamedTab")]
    [InlineData("Use the current page", "CurrentTab")]
    [InlineData("Open a new tab and search React", "NewTab")]
    [InlineData("Search for React", "Unspecified")]
    [InlineData("Click Explore", "Unspecified")]
    public async Task Router_PreservesExplicitSurfaceDispositionInExistingCall(
        string utterance, string disposition)
    {
        var calls = 0;
        var gateway = new FakeGateway((_, questions) =>
        {
            calls++;
            Assert.Contains("particular already-open browser tab", questions["tab_disposition"].Instructions);
            Assert.Contains("is a destination, classified by the separate destination head",
                questions["tab_disposition"].Instructions);
            Assert.Contains("descriptive identity", questions["tab_disposition"].Criteria!["ExistingNamedTab"]);
            Assert.Contains("destination to go to is not a tab reference",
                questions["tab_disposition"].Criteria!["ExistingNamedTab"]);
            return Answers(("route", Choice("COMPUTER_USE")),
                ("intent_completeness", Choice("Actionable")),
                ("media_request_kind", Choice("None")),
                ("destination", Choice("None")),
                ("tab_disposition", Choice(disposition)),
                ("context_dependency", Choice("SelfContained")));
        });
        var result = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(1, calls);
        Assert.Equal(Enum.Parse<TabDisposition>(disposition), result.TabDisposition);
        if (disposition == "ExistingNamedTab")
            Assert.Equal(SemanticDestinationKind.NamedTab, result.DestinationKind);
    }

    [Fact]
    public async Task ArbitraryNamedTabUsesOfferedMetadataAndRejectsDuplicates()
    {
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Contains("tab_8", questions["tab"].Criteria!.Keys);
            Assert.Single(questions);
            Assert.Contains("AMBIGUOUS", questions["tab"].Criteria!.Keys);
            Assert.Contains("NONE", questions["tab"].Criteria!.Keys);
            return Answers(("tab", Choice("tab_8")));
        });
        var router = new TypeSafeCommandRouter(gateway);
        var one = new BrowserTabInfo(8, 1, false, "https://forum.example/",
            "Community forum", BrowserTabProvenance.User);
        Assert.Equal(NamedTabSelection.Select(8), await router.SelectNamedTabAsync("use the community tab", [one]));
        var duplicate = one with { TabId = 9 };
        Assert.Equal(NamedTabSelection.Ambiguous("duplicate_title_and_origin"),
            await router.SelectNamedTabAsync("use the community tab", [one, duplicate]));
    }

    [Theory]
    [InlineData("Open the React tab", "tab_8", 8, "React - Google Search")]
    [InlineData("Open the Agent Island tab", "tab_9", 9, "agent island - Google Search")]
    public async Task NamedTabSelectionUsesPageTitleEvenWhenTabsShareService(
        string utterance, string selectedId, int tabId, string title)
    {
        var calls = 0;
        var log = new CaptureLogger();
        var gateway = new FakeGateway((_, questions) =>
        {
            calls++;
            var candidates = questions["tab"].Criteria!;
            Assert.Single(questions);
            Assert.Contains("React - Google Search", candidates["tab_8"]);
            Assert.Contains("agent island - Google Search", candidates["tab_9"]);
            Assert.Contains("Explore / X", candidates["tab_10"]);
            Assert.Contains("host=www.google.com", candidates["tab_8"]);
            Assert.Contains("service=Google", candidates["tab_8"]);
            Assert.Contains("service=Google", candidates["tab_9"]);
            return Answers(("tab", Choice(selectedId, .92)));
        });
        var tabs = new[]
        {
            new BrowserTabInfo(8, 1, false, "https://www.google.com/search?q=React",
                "React - Google Search", BrowserTabProvenance.User),
            new BrowserTabInfo(9, 1, true, "https://www.google.com/search?q=agent+island",
                "agent island - Google Search", BrowserTabProvenance.User),
            new BrowserTabInfo(10, 1, false, "https://x.com/explore",
                "Explore / X", BrowserTabProvenance.User)
        };
        var route = new CommandRouteDecision(CommandRoute.ComputerUse, .95,
            DestinationKind: SemanticDestinationKind.NamedTab,
            DestinationName: "Google", TabDisposition: TabDisposition.ExistingNamedTab);
        var chrome = new VoiceOS.Core.Candidates.WindowCandidate("chrome", "chrome", "Chrome", true, 42);
        var context = new ExecutionContextSnapshot(chrome, [chrome], true, tabs);
        var decision = await new ScopeResolver().ResolveAsync(utterance,
            route, context, new TypeSafeCommandRouter(gateway, logger: log));
        Assert.Equal(1, calls);
        Assert.Equal(tabId, decision.Browser?.TabId);
        Assert.Equal(tabs.Single(t => t.TabId == tabId).Url, decision.Browser?.ExpectedUrl);
        // A named tab that is already the visible active tab is the current surface.
        var selectedIsActive = tabs.Single(t => t.TabId == tabId).Active;
        Assert.Equal(selectedIsActive ? BrowserScopeKind.ActiveTab : BrowserScopeKind.ExistingNamedTab,
            decision.Browser?.Kind);
        Assert.Equal(selectedIsActive ? null : title, decision.Browser?.ExpectedTitle);
        Assert.True(decision.Browser?.ExplicitSelection);
        Assert.False(decision.Browser?.TabClaimRefuted);
        Assert.Contains(log.Messages, message => message.Contains($"candidate_id={selectedId}")
            && message.Contains(title)
            && message.Contains("www.google.com") && message.Contains("service=Google"));
        Assert.Contains(log.Messages, message => message.Contains($"selected_semantic_value={selectedId}")
            && message.Contains("confidence=0.92") && message.Contains("confidence_floor=0.70"));
    }

    [Fact]
    public async Task EmptyInventoryRefutesNamedTabClaimWithoutASemanticCall()
    {
        var router = new TypeSafeCommandRouter(new FakeGateway((_, _) => throw new Exception("No semantic call expected")));
        var internalOnly = new BrowserTabInfo(3, 1, true, "chrome://newtab/", "New Tab", BrowserTabProvenance.User);
        Assert.Equal(NamedTabSelectionKind.NoMatch,
            (await router.SelectNamedTabAsync("go to the Gmail tab", [internalOnly])).Kind);
    }

    [Fact]
    public async Task NamedTabCandidatesCarryActivityProvenanceAndRecency()
    {
        IReadOnlyDictionary<string, string>? offered = null;
        var gateway = new FakeGateway((_, questions) =>
        {
            offered = questions["tab"].Criteria!;
            return Answers(("tab", Choice("NONE", .9)));
        });
        var tabs = new[]
        {
            new BrowserTabInfo(1, 1, true, "https://www.imdb.com/find?q=sharp", "Sharp Objects - IMDb", BrowserTabProvenance.User),
            new BrowserTabInfo(2, 1, false, "https://docs.python.org/3/", "Python docs", BrowserTabProvenance.VoiceOs, "s2", 40),
            new BrowserTabInfo(3, 1, false, "https://docs.djangoproject.com/", "Django docs", BrowserTabProvenance.VoiceOs, "s3", 55)
        };
        await new TypeSafeCommandRouter(gateway).SelectNamedTabAsync("that docs page", tabs);
        Assert.Contains("active=yes; opened_by=user; recency_rank=unknown", offered!["tab_1"]);
        Assert.Contains("active=no; opened_by=VoiceOS; recency_rank=2", offered["tab_2"]);
        Assert.Contains("active=no; opened_by=VoiceOS; recency_rank=1", offered["tab_3"]);
        Assert.Contains("service=unknown", offered["tab_1"]);
    }

    [Theory]
    [InlineData("Open the React tab", "tab_8", .95, NamedTabSelectionKind.Selected, "-")]
    [InlineData("Open the Vue tab", "NONE", .95, NamedTabSelectionKind.NoMatch, "no_plausible_match")]
    [InlineData("Open the React tab", "AMBIGUOUS", .95, NamedTabSelectionKind.Ambiguous, "multiple_plausible_matches")]
    // A switch needs a confident unique match; a weak selection is not one.
    [InlineData("Open the React tab", "tab_8", .6, NamedTabSelectionKind.Ambiguous, "selection_confidence_below_floor")]
    // NoMatch and Ambiguous stay distinct; refuting a claim needs only ordinary confidence
    // (tabs.result-from-vendor recorded NONE at .69).
    [InlineData("Click the Keychron result", "NONE", .69, NamedTabSelectionKind.NoMatch, "no_plausible_match")]
    [InlineData("Open the Vue tab", "NONE", .44, NamedTabSelectionKind.Unavailable, "choice_confidence_below_threshold")]
    [InlineData("Open the React tab", "tab_8", .44, NamedTabSelectionKind.Unavailable, "choice_confidence_below_threshold")]
    [InlineData("Open the React tab", "tab_77", .95, NamedTabSelectionKind.Unavailable, "candidate_not_offered")]
    public async Task NamedTabSelectionIsTyped(string utterance, string choice, double confidence,
        NamedTabSelectionKind expected, string reason)
    {
        var log = new CaptureLogger();
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Single(questions);
            Assert.Contains("tab_8", questions["tab"].Criteria!.Keys);
            Assert.Contains("tab_9", questions["tab"].Criteria!.Keys);
            return Answers(("tab", Choice(choice, confidence)));
        });
        var tabs = new[]
        {
            new BrowserTabInfo(8, 1, false, "https://www.google.com/search?q=React",
                "React - Google Search", BrowserTabProvenance.User),
            new BrowserTabInfo(9, 1, false, "https://example.org/react",
                "React documentation", BrowserTabProvenance.User)
        };
        var result = await new TypeSafeCommandRouter(gateway, logger: log).SelectNamedTabAsync(utterance, tabs);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(expected == NamedTabSelectionKind.Selected ? 8 : null, result.TabId);
        Assert.Contains(log.Messages, message => message.Contains($"selected_semantic_value={choice}"));
        Assert.Contains(log.Messages, message => message.Contains($"outcome={expected} reason={reason}"));
    }

    [Fact]
    public async Task NamedTabFocusLogsAtomicMetadataVerification()
    {
        var log = new CaptureLogger();
        var service = new BrowserInteractionService(new StaticTransport(),
            new FakeGateway((_, _) => throw new Exception("No semantic call expected")),
            logger: log);
        var outcome = await service.RunAsync("Focus the Andre Karpathy tab", scope: new(
            BrowserScopeKind.ExistingNamedTab, 8,
            "https://www.google.com/search?q=Andre+Karpathy", FocusOnly: true,
            EndState: SemanticEndState.SurfaceReady, GoalShape: GoalShape.SurfaceOnly,
            ExpectedTitle: "Andre Karpathy - Google Search"));
        Assert.Equal(InteractionCompletionState.Complete, outcome.Completion);
        Assert.Contains(log.Messages, message => message.Contains("candidate_id=tab_8")
            && message.Contains("atomic_url_title_verification=passed"));
    }

    [Fact]
    public async Task RefutedNamedTabFallbackNeverCompletesOnArrival()
    {
        // "Click Sharp Objects" carries SurfaceOnly/SurfaceReady heads. On the current tab reached
        // by refuting its named-tab claim, the service must enter the content-action path
        // (normalization) rather than report the visible page as success.
        BrowserExecutionScope Scope(bool refuted) => new(BrowserScopeKind.ActiveTab, 42,
            "https://www.google.com/", EndState: SemanticEndState.SurfaceReady,
            GoalShape: GoalShape.SurfaceOnly, TabClaimRefuted: refuted);
        var service = new BrowserInteractionService(new StaticTransport(),
            new FakeGateway((_, _) => throw new InvalidOperationException("stop at the first semantic call")));

        var control = await service.RunAsync("Click on Sharp Objects", scope: Scope(false));
        Assert.True(control.Completion == InteractionCompletionState.Complete, control.Detail);
        Assert.Equal("The requested browser surface is ready.", control.Detail);

        var refuted = await service.RunAsync("Click on Sharp Objects", scope: Scope(true));
        Assert.NotEqual(InteractionCompletionState.Complete, refuted.Completion);
        Assert.NotEqual("The requested browser surface is ready.", refuted.Detail);
    }

    [Fact]
    public async Task Router_SeparatesNativeInteractionFromBrowser()
    {
        var gateway = new FakeGateway((_, _) => Answers(("route", Choice("NATIVE_INTERACTION", .96)),
            ("intent_completeness", Choice("Actionable", .96))));
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync("click Source Control in VS Code");
        Assert.Equal(CommandRoute.NativeInteraction, route.Route);
    }

    [Fact]
    public async Task SemanticFieldMismatch_CanStopForChoiceInsteadOfTypingQueryIntoAddress()
    {
        var goal = BrowserGoal.FromUtterance("find chicken sandwiches");
        var gateway = new FakeGateway((state, _) =>
        {
            Assert.Contains("Delivery address", JsonSerializer.Serialize(state));
            return Answers(
                ("operation", Choice("BLOCKED", .99)),
                ("stuck", Noul(.99)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var type = new InteractionAction("type-address", InteractionActionKind.TypeText, "address");
        var observation = new InteractionObservation(3, "state",
            "textbox 'Delivery address' near 'Enter delivery address'",
            [new("address", "textbox 'Delivery address' near 'Enter delivery address'", [type])]);

        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Contains(decision.Choices!, choice => choice.Id == "cancel");
    }

    [Fact]
    public async Task AmbiguousResults_ReturnStableOpaqueChoices()
    {
        var goal = BrowserGoal.FromUtterance("find alex");
        var gateway = new FakeGateway((_, _) => Answers(("operation", Choice("BLOCKED", .95)), ("stuck", Noul(.95))));
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var one = new InteractionAction("open-1", InteractionActionKind.Activate, "e1");
        var two = new InteractionAction("open-2", InteractionActionKind.Activate, "e2");
        var observation = new InteractionObservation(7, "same", "two plausible profiles",
            [new("e1", "link 'Alex Smith'", [one]), new("e2", "link 'Alex Jones'", [two])]);

        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.StartsWith("browser:7:", decision.Choices![0].Id);
        Assert.Equal(3, decision.Choices.Count);
    }

    [Fact]
    public async Task FailedActivate_ExcludesActionKindFromNextDecision()
    {
        var goal = BrowserGoal.FromUtterance("open result");
        var click = new InteractionAction("r2:click:e1", InteractionActionKind.Activate, "e1");
        var observation = new InteractionObservation(2, "same", "unchanged result", [new("e1", "link 'Result'", [click])]);
        var history = new InteractionHistoryEntry[]
        {
            new("same", click with { Id = "r1:click:e1" },
                InteractionActionResult.Fail(InteractionResultStatus.OccludedTarget, "covered"),
                "same", false, DateTimeOffset.UtcNow)
        };
        var gateway = new FakeGateway((_, questions) =>
        {
            // The only activate candidate was excluded by the failed-action filter
            Assert.DoesNotContain("CLICK", questions["operation"].Criteria!.Keys);
            return Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99)));
        });

        var decision = await new TypeSafeBrowserDecisionSource(gateway, goal).DecideAsync(
            new(new(goal.OriginalUtterance), observation, history, new(), new(2, 1, 0, 1)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
    }

    [Fact]
    public void BrowserGoal_DoesNotInferScopeFromWords()
    {
        var goal = BrowserGoal.FromUtterance("visit https://example.org on a service");
        Assert.Null(goal.ExplicitUrl);
        Assert.Null(goal.NamedServiceHint);
    }

    [Fact]
    public void NormalizedTextCandidates_KeepDiscoveryQueryAndLocalEntity()
    {
        var goal = BrowserGoal.FromUtterance("search for chicken sandwich on Uber Eats") with
        {
            Normalization = new("Find chicken sandwich on Uber Eats", "chicken sandwich", "food", "Uber Eats",
                "https://www.ubereats.com/", ["Chicken Sandwich Uber Eats"], "Results are open", [])
        };
        Assert.Equal(["Chicken Sandwich Uber Eats", "chicken sandwich"],
            BrowserTextCandidates.From(goal).Select(candidate => candidate.Text));
    }

    [Fact]
    public void TextCandidatesRequireSemanticNormalization()
        => Assert.Empty(BrowserTextCandidates.From(BrowserGoal.FromUtterance("look something up")));

    [Fact]
    public async Task Service_NormalizesEveryActionGoalAndPreservesRawGoal()
    {
        var normalizer = new FakeNormalizer();
        var seen = new List<string>();
        var gateway = new FakeGateway((state, _) =>
        {
            var json = JsonSerializer.Serialize(state);
            seen.Add(json);
            return Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99)));
        });
        var service = new BrowserInteractionService(new StaticTransport(), gateway, normalizer);
        await service.RunAsync("search for Andrew Huberman");
        Assert.Equal(1, normalizer.Calls);
        await service.RunAsync("Find the Ripcrap repository on github");
        Assert.Equal(2, normalizer.Calls);
        Assert.Contains(seen, x => x.Contains("Find the Ripcrap repository on github") && x.Contains("ripgrep"));
    }

    [Fact]
    public async Task Service_UsesActivationIdAsOwnedBrowserSession()
    {
        var activationId = Guid.NewGuid().ToString("N");
        var transport = new StaticTransport();
        var gateway = new FakeGateway((_, _) => Answers(
            ("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99))));
        var service = new BrowserInteractionService(transport, gateway, new FakeNormalizer());

        await service.RunAsync("search hello world", activationId: activationId);

        Assert.Equal(activationId, transport.LastSession);
    }

    [Fact]
    public async Task NewTabOnlyUsesNormalChromeTabWithoutGoalModel()
    {
        var transport = new StaticTransport();
        var gateway = new FakeGateway((_, _) => throw new Exception("No action decision expected"));
        var service = new BrowserInteractionService(transport, gateway);
        var outcome = await service.RunAsync("make a fresh tab", scope: new(
            BrowserScopeKind.NewTaskTab, EndState: SemanticEndState.SurfaceReady,
            GoalShape: GoalShape.SurfaceOnly));
        Assert.Equal(InteractionCompletionState.Complete, outcome.Completion);
        Assert.Equal(1, transport.NormalTabs);
        Assert.Equal(0, transport.Opens);
    }

    [Fact]
    public async Task SurfaceOnlyWithPendingNamedDestinationDoesNotCompleteOnBlankTab()
    {
        var transport = new StaticTransport();
        var normalizer = new FakeNormalizer
        {
            Result = FakeNormalizer.Normalized with
            {
                Objective = "open the service", Entity = null, PreferredService = "Reddit",
                SearchQueries = [], CorrectedTerms = [], EndState = SemanticEndState.SurfaceReady
            }
        };
        var gateway = new FakeGateway((_, _) => Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99))));
        var service = new BrowserInteractionService(transport, gateway, normalizer);
        await service.RunAsync("open the service", scope: new(BrowserScopeKind.NewTaskTab,
            EndState: SemanticEndState.SurfaceReady, GoalShape: GoalShape.SurfaceOnly,
            DestinationPending: true));
        Assert.Equal(0, transport.NormalTabs);
        Assert.Equal(1, normalizer.Calls);
        Assert.Equal("https://www.reddit.com/", transport.LastOpenUrl);
    }

    [Fact]
    public async Task SurfaceOnlyKnownDestinationCompletesOnlyOnceDestinationContentLoads()
    {
        var reached = new StaticTransport();
        var service = new BrowserInteractionService(reached,
            new FakeGateway((_, _) => throw new Exception("No action decision expected")));
        var scope = new BrowserExecutionScope(BrowserScopeKind.NewTaskTab,
            Destination: new("https://www.instagram.com/"), NamedServiceHint: "Instagram",
            EndState: SemanticEndState.SurfaceReady, GoalShape: GoalShape.SurfaceOnly);
        var ok = await service.RunAsync("open the service on the web", scope: scope);
        Assert.Equal(InteractionCompletionState.Complete, ok.Completion);
        Assert.Equal("https://www.instagram.com/", reached.LastOpenUrl);

        var blank = new StaticTransport { SnapshotUrl = "chrome://newtab/" };
        var failed = await new BrowserInteractionService(blank,
            new FakeGateway((_, _) => throw new Exception("No action decision expected")))
            .RunAsync("open the service on the web", scope: scope);
        Assert.Equal(InteractionCompletionState.Incomplete, failed.Completion);
    }

    [Fact]
    public async Task SurfaceOnlySelectionCompletesWithoutNormalization()
    {
        var transport = new StaticTransport();
        var normalizer = new FakeNormalizer();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("No action decision expected")),
            normalizer);
        var result = await service.RunAsync("bring that tab forward", scope: new(
            BrowserScopeKind.ExistingNamedTab, 42, "https://www.google.com/",
            FocusOnly: true, EndState: SemanticEndState.SurfaceReady,
            GoalShape: GoalShape.SurfaceOnly));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(0, normalizer.Calls);
        Assert.Equal(1, transport.Selections);
    }

    [Theory]
    [InlineData(BrowserScopeKind.ActiveTab, "on this page, change the selected option")]
    [InlineData(BrowserScopeKind.ExistingNamedTab, "use the open project tab and update the view")]
    [InlineData(BrowserScopeKind.ActiveTab, "in this tab, find an item and open its details")]
    public async Task SurfaceReferenceWithRemainingGoalRunsSemanticExecution(
        BrowserScopeKind kind, string utterance)
    {
        var transport = new StaticTransport();
        var normalizer = new FakeNormalizer();
        var calls = 0;
        var gateway = new FakeGateway((_, _) =>
        {
            calls++;
            return Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99)));
        });
        var service = new BrowserInteractionService(transport, gateway, normalizer);
        var result = await service.RunAsync(utterance, scope: new(kind, 42,
            "https://www.google.com/", EndState: SemanticEndState.SurfaceReady,
            GoalShape: GoalShape.ActionOnSurface));
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(1, normalizer.Calls);
        Assert.Equal(1, transport.Selections);
        Assert.True(calls > 0);
    }

    [Fact]
    public async Task Service_NormalizesCompoundSearchOnceBeforeOfferingText()
    {
        var normalizer = new FakeNormalizer
        {
            Result = FakeNormalizer.Normalized with
            {
                Objective = "play Kendrick Lamar music",
                Entity = "Kendrick Lamar",
                SearchQueries = ["Kendrick Lamar"]
            }
        };
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Contains("TYPE_TEXT", questions["operation"].Criteria!.Keys);
            Assert.DoesNotContain(questions["operation"].Criteria!.Values,
                text => text.Contains("and play something", StringComparison.OrdinalIgnoreCase));
            return Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99)));
        });
        var service = new BrowserInteractionService(new StaticTransport(), gateway, normalizer);
        await service.RunAsync("search for Kendrick Lamar and play something");
        Assert.Equal(1, normalizer.Calls);
    }


    [Fact]
    public async Task ActiveChromeDisappearsBeforeExecution_StopsBeforeSelection()
    {
        var transport = new StaticTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("No semantic call expected")),
            foregroundVerifier: _ => false);
        var scope = new BrowserExecutionScope(BrowserScopeKind.ActiveTab, 7,
            "https://www.google.com/", ExplicitSelection: true,
            RequireForegroundChrome: true, ExpectedForegroundHandle: 42);
        var result = await service.RunAsync("search React", scope: scope);
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal(0, transport.Opens);
    }

    [Fact]
    public async Task FailedNormalization_StopsBeforeOpeningTab()
    {
        var transport = new StaticTransport();
        var service = new BrowserInteractionService(transport,
            new FakeGateway((_, _) => throw new Exception("Jev should not be called")),
            new FakeNormalizer { Fail = true });
        var result = await service.RunAsync("Find the Ripcrap repository on github");
        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal(0, transport.Opens);
    }

    [Fact]
    public async Task MalformedProviderResponse_FailsWithoutLeakingCredential()
    {
        var handler = new StubHttpHandler();
        var normalizer = new OpenRouterBrowserGoalNormalizer(new HttpClient(handler), "placeholder-credential");
        var result = await normalizer.NormalizeAsync("find a repository on a service");
        Assert.Null(result);
        Assert.Equal(1, handler.Requests);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"invalid json\"}}]}") });
        }
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=ripgrep+github", "ripgrep - GitHub result", false)]
    [InlineData("https://github.com/BurntSushi/ripgrep", "BurntSushi/ripgrep", true)]
    public async Task NormalizedCompletion_RequiresDestinationPage(string url, string title, bool complete)
    {
        var goal = BrowserGoal.FromUtterance("Find the Ripcrap repository on github") with
        { Normalization = FakeNormalizer.Normalized };
        var gateway = new FakeGateway((state, questions) =>
        {
            Assert.Contains("view the ripgrep repository on GitHub", JsonSerializer.Serialize(state));
            Assert.Contains("destination", questions["goal_achieved"].Instructions);
            return complete
                ? Answers(("operation", Choice("DONE", .99)), ("goal_achieved", Noul(.99)))
                : Answers(("operation", Choice("DONE", .99)), ("goal_achieved", Noul(.01)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var evidence = JsonSerializer.Serialize(new { current_url = url, current_title = title });
        var observation = new InteractionObservation(1, url, evidence, []);
        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(complete ? InteractionCompletionState.Complete : InteractionCompletionState.Uncertain, decision.Completion);
    }

    [Fact]
    public async Task NormalizedGoal_CannotSelectUnofferedElementRef()
    {
        var goal = BrowserGoal.FromUtterance("find the repository on a service") with
        { Normalization = FakeNormalizer.Normalized };
        var gateway = new FakeGateway((_, _) => Answers(("operation", Choice("CLICK", .99)),
            ("click_target", Choice("invented-ref", .99))));
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var action = new InteractionAction("click-real", InteractionActionKind.Activate, "real-ref");
        var observation = new InteractionObservation(1, "state", "{}",
            [new("real-ref", "link 'real result'", [action])]);
        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Null(decision.Action);
    }

    [Theory]
    [InlineData("missing", "unoffered_operation")]
    [InlineData("low", "low_operation_confidence")]
    [InlineData("blocked", "blocked")]
    [InlineData("target", "unoffered_operation")]
    public async Task NonActionDecision_LogsItsSpecificExit(string response, string reason)
    {
        var log = new CaptureLogger();
        var gateway = new FakeGateway((_, _) => response switch
        {
            "missing" => Answers(),
            "low" => Answers(("operation", Choice("CLICK", .1))),
            "blocked" => Answers(("operation", Choice("BLOCKED", .9)), ("stuck", Noul(.9))),
            _ => Answers(("operation", Choice("a999", .9)))
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, BrowserGoal.FromUtterance("open result"), logger: log);
        var action = new InteractionAction("a", InteractionActionKind.Activate, "e1");
        var observation = new InteractionObservation(1, "state", "{}", [new("e1", "link result", [action])]);
        var decision = await source.DecideAsync(new(new("open result"), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Contains(log.Messages, message => message.Contains($"reason={reason}"));
    }

    [Fact]
    public async Task EmptyFieldCriteria_HasOneTextOperationAndKeepsConfidenceGuard()
    {
        var goal = BrowserGoal.FromUtterance("search for ripgrep");
        var gateway = new FakeGateway((_, questions) =>
        {
            var options = questions["operation"].Criteria!;
            Assert.Contains("TYPE_TEXT", options.Keys);
            Assert.Contains("CLICK", options.Keys);
            Assert.Contains("SCROLL_DOWN", options.Keys);
            Assert.Contains("DONE", options.Keys);
            Assert.Contains("BLOCKED", options.Keys);
            Assert.Single(questions["text_target"].Criteria!);
            return Answers(("operation", Choice("TYPE_TEXT", .31)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var observation = new InteractionObservation(1, "state", "{}", [
            new("q", "searchbox 'Search' value=''", [new("set-q", InteractionActionKind.SetText, "q")]),
            new("search", "button 'Search'", [new("click-search", InteractionActionKind.Activate, "search")]),
            new("page", "Current page", [new("scroll", InteractionActionKind.Scroll, Direction: "down")])
        ]);
        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Null(decision.Action);
    }

    [Fact]
    public async Task EmptyFieldSelectedText_StillMustBeAnOfferedCandidate()
    {
        var goal = BrowserGoal.FromUtterance("search for ripgrep");
        var gateway = new FakeGateway((_, _) => Answers(("operation", Choice("TYPE_TEXT")),
            ("text_target", Choice("invented"))));
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var observation = new InteractionObservation(1, "state", "{}",
            [new("q", "searchbox 'Search' value=''", [new("set-q", InteractionActionKind.SetText, "q")])]);
        var decision = await source.DecideAsync(new(new(goal.OriginalUtterance), observation, [], new(), new(0, 0, 0, 0)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Null(decision.Action);
    }

    [Fact]
    public async Task Done_CompletesAfterOneFreshSemanticObservationWithDifferentStateKey()
    {
        var goal = BrowserGoal.FromUtterance("find repository on GitHub") with { Normalization = FakeNormalizer.Normalized };
        var calls = 0;
        var gateway = new FakeGateway((_, _) =>
        {
            calls++;
            return Answers(("operation", Choice("DONE", .99)), ("goal_achieved", Noul(.99)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var evidence = JsonSerializer.Serialize(new { current_url = "https://github.com/BurntSushi/ripgrep", current_title = "ripgrep", visible_text = "repository" });
        var first = new InteractionObservation(1, "dom-state-a", evidence, []);
        var second = new InteractionObservation(2, "dom-state-b", evidence.Replace("repository", "repository updated"), []);
        var surface = new CompletionSurface(source, first, second);
        var result = await new InteractionEngine().RunAsync(new(goal.OriginalUtterance), surface, source);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, surface.Observations);
        Assert.Equal(2, calls);
    }

    private sealed class CompletionSurface(TypeSafeBrowserDecisionSource source, params InteractionObservation[] observations) : IInteractionSurface
    {
        public int Observations { get; private set; }
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(observations[Math.Min(Observations++, observations.Length - 1)]);
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No action expected");
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> recentHistory, CancellationToken cancellationToken = default)
            => source.AssessAsync(BrowserGoal.FromUtterance(goal.Text), observation, recentHistory, cancellationToken);
    }

    private sealed class CaptureLogger : ILogger,
        ILogger<TypeSafeCommandRouter>, ILogger<BrowserInteractionService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
        private sealed class NullScope : IDisposable { public static NullScope Instance { get; } = new(); public void Dispose() { } }
    }

    private sealed class FakeNormalizer : IBrowserGoalNormalizer
    {
        public static BrowserGoalNormalization Normalized => new(
            "view the ripgrep repository on GitHub", "ripgrep", "repository", "GitHub",
            "https://github.com/", ["ripgrep", "ripgrep github"],
            "GitHub repository page for ripgrep is open", [new("Ripcrap", "ripgrep", .9)]);
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public BrowserGoalNormalization Result { get; set; } = Normalized;
        public ValueTask<BrowserGoalNormalization?> NormalizeAsync(string utterance, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<BrowserGoalNormalization?>(Fail ? null : Result);
        }
    }

    private sealed class StaticTransport : IChromeCompanionTransport
    {
        public string? LastSession { get; private set; }
        public int Opens { get; private set; }
        public int NormalTabs { get; private set; }
        public int Selections { get; private set; }
        public string? LastOpenUrl { get; private set; }
        public string SnapshotUrl { get; init; } = "https://www.google.com/";
        public ValueTask SelectTabAsync(string sessionId, int tabId, string expectedUrl,
            bool requireActive, CancellationToken cancellationToken = default)
        {
            Selections++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<BrowserTabInfo> CreateNewTabAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            NormalTabs++;
            return ValueTask.FromResult(new BrowserTabInfo(52, 1, true, "chrome://newtab/",
                "New Tab", BrowserTabProvenance.VoiceOs, sessionId, 1));
        }
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
        {
            Opens++;
            LastSession = sessionId;
            LastOpenUrl = url;
            return ValueTask.FromResult(Snapshot(sessionId));
        }
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(sessionId));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(action.SessionId));
        private BrowserSnapshot Snapshot(string session) => new(42, session, "r1", SnapshotUrl, "Google", "Search",
            false, new(1280, 800, 0, 0), [], false);
    }

    [Fact]
    public void Parser_PreservesSelectedChoiceConfidenceAndNumericFields()
    {
        using var document = JsonDocument.Parse("""{"type":"choice","choice":"b","confidence":0.71,"a":0.8,"b":0.2}""");
        var answer = JevAnswerParser.Parse(document.RootElement);
        Assert.Equal("choice", answer.QuestionType);
        Assert.Equal("b", answer.SelectedChoice);
        Assert.Equal(.71, answer.Confidence, 3);
        Assert.Equal(.8, answer.Probabilities["a"], 3);
        Assert.Equal(.2, answer.Probabilities["b"], 3);
    }

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
            object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions,
            CancellationToken cancellationToken = default)
            => Task.FromResult(answer(state, questions));
    }
}
