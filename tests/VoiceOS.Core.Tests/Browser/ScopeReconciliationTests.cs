using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ScopeReconciliationTests
{
    private static readonly WindowCandidate Chrome = new("chrome", "chrome", "Fixture page", true, 42);
    private static ExecutionContextSnapshot Context(bool connected = true, bool foreground = true, int activeCount = 1)
        => new(foreground ? Chrome : new("editor", "editor", "Editor", true, 75), [], connected,
            Enumerable.Range(1, activeCount).Select(id => new BrowserTabInfo(id, id, true,
                "https://fixture.example/results", "Fixture results", BrowserTabProvenance.User)).ToArray());

    private static async Task<CommandRouteDecision> Route(Gateway gateway, string utterance = "Open the cobalt entry")
        => await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);

    [Theory]
    [InlineData("Open the cobalt entry", false)]
    [InlineData("Open the official site", false)]
    [InlineData("Open the 2021 one", true)]
    public async Task EstablishedPageDependencySurvivesCoarseUncertainty(string utterance, bool refuted)
    {
        var gateway = new Gateway { Dependency = "RequiresCurrentSurface", NamedTab = refuted,
            EndState = "SurfaceReady", Shape = "SurfaceOnly" };
        var route = await Route(gateway, utterance);
        Assert.Equal(RoutingReason.LowConfidence, route.Reason);
        var scope = await new ScopeResolver().ResolveAsync(utterance, route, Context(), new TypeSafeCommandRouter(gateway));
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser?.Kind);
        Assert.Equal(refuted, scope.Browser!.TabClaimRefuted);
        Assert.False(scope.Browser.IsSurfaceOnly);
        Assert.False(scope.Browser.AcquiresSurface);
        Assert.False(scope.Browser.ExplicitSelection);
    }

    [Theory]
    [InlineData("Open QuasarDesk in the browser", .44)]
    [InlineData("Open MarbleNest in the browser", .38)]
    [InlineData("Open Discord in the browser", .44)]
    public async Task UnregisteredExplicitBrowserEntityNeedsExecutionBeyondAcquisition(string utterance, double confidence)
    {
        var gateway = new Gateway { Preference = "Browser", Entity = "NamedEntity",
            Shape = "SurfaceOnly", EndState = "SurfaceReady", RouteConfidence = confidence };
        var route = await Route(gateway, utterance);
        Assert.Null(route.DestinationName);
        var scope = await new ScopeResolver().ResolveAsync(utterance, route, Context(foreground: false));
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser?.Kind);
        Assert.True(scope.Browser!.DestinationPending);
        Assert.False(scope.Browser.IsSurfaceOnly);
    }

    [Theory]
    [InlineData("Look up tidal turbine efficiency")]
    [InlineData("Find information about lunar basalt")]
    [InlineData("What's the weather in Paris right now?")]
    public async Task FreshResultsDoNotHijackUnrelatedActivePage(string utterance)
    {
        var gateway = new Gateway { RouteConfidence = .32 };
        var route = await Route(gateway, utterance);
        var scope = await new ScopeResolver().ResolveAsync(utterance, route, Context());
        Assert.Equal(BrowserScopeKind.NewTaskTab, scope.Browser?.Kind);
        Assert.Null(scope.Browser!.TabId);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("native")]
    [InlineData("text")]
    [InlineData("direct")]
    [InlineData("unsupported")]
    [InlineData("unknown")]
    [InlineData("transport")]
    [InlineData("return")]
    [InlineData("uncertain_goal")]
    [InlineData("uncertain_relation")]
    public async Task FreshSemanticsAloneCannotEstablishBrowserCapability(string exclusion)
    {
        var gateway = new Gateway();
        switch (exclusion)
        {
            case "incomplete": gateway.Complete = "Incomplete"; break;
            case "native": gateway.Preference = "Native"; break;
            case "text": gateway.CoarseRoute = "TEXT_TRANSFORM"; gateway.RouteConfidence = .98; break;
            case "direct": gateway.CoarseRoute = "DIRECT_CAPABILITY"; gateway.RouteConfidence = .98; break;
            case "unsupported": gateway.EndState = "OtherBoundedGoal"; break;
            case "unknown": gateway.CoarseRoute = "CLARIFY"; break;
            case "transport": gateway.Media = "Uncertain"; break;
            case "return": gateway.Return = "NavigationHistory"; break;
            case "uncertain_goal": gateway.Shape = "Uncertain"; break;
            case "uncertain_relation": gateway.Relation = "Uncertain"; break;
        }
        var route = await Route(gateway);
        var scope = await new ScopeResolver().ResolveAsync("Fixture task", route, Context());
        Assert.NotEqual(ExecutionScopeKind.Browser, scope.Kind);
        if (exclusion == "text") Assert.Equal(ExecutionScopeKind.TextTransform, scope.Kind);
        if (exclusion == "direct") Assert.Equal(ExecutionScopeKind.DirectCapability, scope.Kind);
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("foreground")]
    [InlineData("disconnected")]
    [InlineData("multiple")]
    [InlineData("no_tab")]
    [InlineData("incomplete")]
    [InlineData("router_failure")]
    [InlineData("native")]
    [InlineData("text")]
    [InlineData("ambiguous_tab")]
    public async Task RefutedTabRecoveryRequiresEverySafetyCondition(string exclusion)
    {
        var gateway = new Gateway { Dependency = "RequiresCurrentSurface", NamedTab = true };
        if (exclusion == "dependency") gateway.Dependency = "SelfContained";
        if (exclusion == "incomplete") gateway.Complete = "Incomplete";
        if (exclusion == "native") gateway.Preference = "Native";
        if (exclusion == "text") { gateway.CoarseRoute = "TEXT_TRANSFORM"; gateway.RouteConfidence = .98; }
        if (exclusion == "ambiguous_tab") gateway.TabAnswer = "AMBIGUOUS";
        var route = await Route(gateway);
        if (exclusion == "router_failure") route = route with { Reason = RoutingReason.RouterFailure };
        var context = Context(exclusion != "disconnected", exclusion != "foreground",
            exclusion == "multiple" ? 2 : exclusion == "no_tab" ? 0 : 1);
        var scope = await new ScopeResolver().ResolveAsync("Open the cobalt entry", route, context,
            new TypeSafeCommandRouter(gateway));
        Assert.NotEqual(ExecutionScopeKind.Browser, scope.Kind);
    }

    [Theory]
    [InlineData(RoutingReason.LowConfidence)]
    [InlineData(RoutingReason.AmbiguousIntent)]
    public async Task RefutedFallbackAcceptsOnlyPreliminaryUncertainty(RoutingReason reason)
    {
        var gateway = new Gateway { Dependency = "RequiresCurrentSurface", NamedTab = true };
        var route = await Route(gateway);
        var scope = await new ScopeResolver().ResolveAsync("Open the cobalt entry", route with { Reason = reason },
            Context(), new TypeSafeCommandRouter(gateway));
        Assert.True(scope.Browser?.TabClaimRefuted);
    }

    [Theory]
    [InlineData(.46, true)] // Recorded established dependency survives a .35 coarse route.
    [InlineData(.40, false)]
    [InlineData(.31, false)] // Latest isolated live replay: dependency is not established.
    public async Task RefutedFallbackPreservesDependencyConfidenceBoundary(double confidence, bool expectedBrowser)
    {
        var gateway = new Gateway { Dependency = "RequiresCurrentSurface", NamedTab = true,
            Shape = "SurfaceOnly", EndState = "SurfaceReady", DependencyConfidence = confidence };
        var scope = await new ScopeResolver().ResolveAsync("Open the 2021 one", await Route(gateway),
            Context(), new TypeSafeCommandRouter(gateway));
        Assert.Equal(expectedBrowser, scope.Kind == ExecutionScopeKind.Browser);
        if (expectedBrowser) Assert.True(scope.Browser!.TabClaimRefuted);
    }

    [Fact]
    public async Task SafeExecutableDirectOfferBlocksFreshBrowserRecoveryAndIsReadOnce()
    {
        var gateway = new Gateway { Pick = "DirectCapability" };
        var route = await Route(gateway);
        var calls = 0;
        var offer = new DirectOffer("Set volume", new VoiceProgram([new SetVolumeStep("one", 30)]));
        var scope = await new ScopeResolver().ResolveAsync("Fixture task", route, Context(),
            new TypeSafeCommandRouter(gateway), directOffer: _ =>
            {
                calls++;
                return ValueTask.FromResult<DirectOffer?>(offer);
            });
        Assert.Equal(ExecutionScopeKind.DirectCapability, scope.Kind);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("TEXT_TRANSFORM")]
    [InlineData("NATIVE_INTERACTION")]
    [InlineData("DIRECT_CAPABILITY")]
    public async Task ExplicitBrowserPreferenceDoesNotOverrideCompetingCoarseCapability(string coarse)
    {
        var gateway = new Gateway { CoarseRoute = coarse, Preference = "Browser", Entity = "NamedEntity",
            EndState = "SurfaceReady", Shape = "SurfaceOnly" };
        var scope = await new ScopeResolver().ResolveAsync("Open QuasarDesk in the browser", await Route(gateway), Context());
        Assert.Equal(ExecutionScopeKind.Unresolved, scope.Kind);
    }

    [Theory]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 2)]
    public async Task CurrentPageRecoveryCannotGuessMissingOrAmbiguousSurface(bool connected, bool foreground, int count)
    {
        var route = await Route(new Gateway { Dependency = "RequiresCurrentSurface" });
        var scope = await new ScopeResolver().ResolveAsync("Open the cobalt entry", route, Context(connected, foreground, count));
        Assert.Equal(ExecutionScopeKind.Unresolved, scope.Kind);
    }

    [Theory]
    [InlineData("Unspecified")]
    [InlineData("Native")]
    public async Task InstalledServiceAcquisitionRetainsNativePolicyDuringCoarseUncertainty(string preference)
    {
        var gateway = new Gateway { Destination = "Spotify", Entity = "NamedEntity", Preference = preference,
            EndState = "SurfaceReady", Shape = "SurfaceOnly" };
        var context = Context() with { InstalledApps = [new AppCandidate("service-app", "Spotify", "fixture-app")] };
        var scope = await new ScopeResolver().ResolveAsync("Open Spotify", await Route(gateway, "Open Spotify"), context);
        Assert.Equal(ExecutionScopeKind.NativeInteraction, scope.Kind);
        Assert.Equal("service-app", scope.Native?.AppCandidateId);
    }

    [Theory]
    [InlineData(.40)]
    [InlineData(.98)]
    public async Task PickerReceivesSemanticsAndSelectsPageForObservationDespiteUncertainDependency(double routeConfidence)
    {
        var gateway = new Gateway { CoarseRoute = "COMPUTER_USE", RouteConfidence = routeConfidence,
            Dependency = "Uncertain", Preference = "Browser", Shape = "SurfaceOnly", EndState = "ResourceOpened",
            Pick = "ActiveBrowserTab" };
        var router = new TypeSafeCommandRouter(gateway);
        var route = await router.RouteAsync("Open the official site");
        var context = Context();
        context = context with { FrontDoor = FrontDoorContextBuilder.Build("Open the official site", context, null, DateTimeOffset.UtcNow) };
        var scope = await new ScopeResolver().ResolveAsync("Open the official site", route, context, router);
        Assert.Equal(BrowserScopeKind.ActiveTab, scope.Browser?.Kind);
        Assert.False(scope.Browser!.IsSurfaceOnly);
        var semantics = gateway.PickerState!.Value.GetProperty("semantics");
        Assert.Equal(route.Route.ToString(), gateway.PickerState.Value.GetProperty("preliminaryRoute").GetString());
        Assert.Equal(route.Reason.ToString(), gateway.PickerState.Value.GetProperty("routeReason").GetString());
        Assert.True(semantics.GetProperty("IntentActionable").GetBoolean());
        Assert.Equal("Uncertain", semantics.GetProperty("contextDependency").GetString());
        Assert.Equal("Browser", semantics.GetProperty("surfacePreference").GetString());
        Assert.Equal("NewTask", semantics.GetProperty("taskRelation").GetString());
        Assert.Equal("SurfaceOnly", semantics.GetProperty("goalShape").GetString());
        Assert.Contains("observation", gateway.PickerInstruction);
        Assert.Contains("an unresolved target identity alone does not require Clarify", gateway.PickerInstruction);
    }

    private sealed class Gateway : IJevGateway
    {
        public string CoarseRoute = "COMPUTER_USE", Dependency = "SelfContained", Preference = "Unspecified", Destination = "None",
            Entity = "Uncertain", Shape = "ActionOnSurface", EndState = "ResultsVisible", Complete = "Actionable",
            Media = "None", Return = "None", Relation = "NewTask", TabAnswer = "NONE", Pick = "Clarify";
        public bool NamedTab;
        public double RouteConfidence = .35;
        public double DependencyConfidence = .98;
        public JsonElement? PickerState;
        public string? PickerInstruction;
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            JevAnswer Head(string value, double confidence = .98) => new("choice", value,
                new Dictionary<string, double> { [value] = .99 }, confidence);
            if (questions.ContainsKey("tab")) return Result(new() { ["tab"] = Head(TabAnswer) });
            if (questions.ContainsKey("surface"))
            {
                PickerState = JsonSerializer.SerializeToElement(state);
                PickerInstruction = JsonSerializer.Serialize(questions["surface"]);
                return Result(new() { ["surface"] = Head(Pick) });
            }
            return Result(new()
            {
                ["route"] = new("choice", CoarseRoute, new Dictionary<string, double>
                    { ["COMPUTER_USE"] = .49, ["CLARIFY"] = .38, ["DIRECT_CAPABILITY"] = .13 }, RouteConfidence),
                ["intent_completeness"] = Head(Complete), ["destination"] = Head(Destination),
                ["tab_disposition"] = Head(NamedTab ? "ExistingNamedTab" : "Unspecified"),
                ["context_dependency"] = Head(Dependency, DependencyConfidence), ["surface_preference"] = Head(Preference),
                ["requested_entity"] = Head(Entity), ["goal_shape"] = Head(Shape), ["end_state"] = Head(EndState),
                ["task_relation"] = Head(Relation), ["media_request_kind"] = Head(Media), ["return_target"] = Head(Return)
            });
        }
        private static Task<IReadOnlyDictionary<string, JevAnswer>> Result(Dictionary<string, JevAnswer> answers)
            => Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(answers);
    }
}
