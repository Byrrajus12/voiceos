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
                ("media_op", Choice(expected.ToString(), .96)));
        });
        var route = await new TypeSafeCommandRouter(gateway).RouteAsync(utterance);
        Assert.Equal(CommandRoute.DirectCapability, route.Route);
        Assert.Equal(RoutingReason.MediaTransport, route.Reason);
        Assert.Equal(MediaRequestKind.Transport, route.MediaRequestKind);
        Assert.Equal(expected, route.MediaOperation);
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

    [Theory]
    [InlineData("search for chicken shawarma on Uber Eats", "https://www.ubereats.com/")]
    [InlineData("play Runaway on YouTube", "https://www.youtube.com/")]
    [InlineData("find ripgrep on GitHub", "https://github.com/")]
    [InlineData("play Kendrick Lamar on Spotify", "https://open.spotify.com/")]
    [InlineData("find a book on Unknown Service", "https://www.google.com/")]
    public void BrowserGoal_UsesOnlyTrustedNamedServiceOrigin(string utterance, string expected)
        => Assert.Equal(expected, BrowserGoal.BootstrapUrl(BrowserGoal.FromUtterance(utterance)));

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
    public void BrowserGoal_ParsesNamedServiceHint()
    {
        var goal = BrowserGoal.FromUtterance("find chicken sandwiches on Uber Eats");
        Assert.Equal("Uber Eats", goal.NamedServiceHint);
        Assert.Null(goal.ExplicitUrl);
        Assert.False(goal.ExplicitCurrentTabIntent);
    }

    [Fact]
    public void BrowserGoal_ParsesExplicitUrl()
    {
        var goal = BrowserGoal.FromUtterance("open https://github.com/ripgrep");
        Assert.Equal("https://github.com/ripgrep", goal.ExplicitUrl!.AbsoluteUri);
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

    [Theory]
    [InlineData("search Kendrick Lamar")]
    [InlineData("search Andrew Huberman")]
    [InlineData("open https://example.com/")]
    [InlineData("https://example.com/")]
    public void Grounding_SkipsSingleLiteralInteraction(string utterance)
    {
        Assert.True(BrowserGrounding.IsSufficient(BrowserGoal.FromUtterance(utterance), out _));
    }

    [Theory]
    [InlineData("search for Kendrick Lamar and play something")]
    [InlineData("search for the React repository and open it")]
    [InlineData("find the official React documentation for useEffect")]
    [InlineData("find a flight to Miami next Friday")]
    [InlineData("Find the Ripcrap repository on github")]
    [InlineData("open https://example.com/ and read the latest post")]
    public void Grounding_NormalizesGoalsBeyondSingleLiteralInteraction(string utterance)
    {
        Assert.False(BrowserGrounding.IsSufficient(BrowserGoal.FromUtterance(utterance), out _));
    }

    [Fact]
    public void Grounding_OrdinalReferenceNeedsVisibleOrderedLinks()
    {
        var goal = BrowserGoal.FromUtterance("open the second one");
        Assert.False(BrowserGrounding.IsSufficient(goal, out _));
        var click = new InteractionAction("click", InteractionActionKind.Activate, "e1");
        var observation = new InteractionObservation(1, "results", "visible search results",
        [
            new("e1", "link 'First result'", [click]),
            new("e2", "link 'Second result'", [click with { TargetId = "e2" }])
        ]);
        Assert.True(BrowserGrounding.IsSufficient(goal, out _, observation));
    }

    [Fact]
    public async Task Service_SkipsOrCallsNormalizerOnceAndPreservesRawGoal()
    {
        var normalizer = new FakeNormalizer();
        var gateway = new FakeGateway((state, _) =>
        {
            var json = JsonSerializer.Serialize(state);
            Assert.Contains("Find the Ripcrap repository on github", json);
            Assert.Contains("ripgrep", json);
            return Answers(("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99)));
        });
        var service = new BrowserInteractionService(new StaticTransport(), gateway, normalizer);
        await service.RunAsync("search for Andrew Huberman");
        Assert.Equal(0, normalizer.Calls);
        await service.RunAsync("Find the Ripcrap repository on github");
        Assert.Equal(1, normalizer.Calls);
    }

    [Fact]
    public async Task Service_UsesActivationIdAsOwnedBrowserSession()
    {
        var activationId = Guid.NewGuid().ToString("N");
        var transport = new StaticTransport();
        var gateway = new FakeGateway((_, _) => Answers(
            ("operation", Choice("BLOCKED", .99)), ("stuck", Noul(.99))));
        var service = new BrowserInteractionService(transport, gateway);

        await service.RunAsync("search hello world", activationId: activationId);

        Assert.Equal(activationId, transport.LastSession);
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
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"invalid json\"}}]") });
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

    private sealed class CaptureLogger : ILogger
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
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
        {
            Opens++;
            LastSession = sessionId;
            return ValueTask.FromResult(Snapshot(sessionId));
        }
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(sessionId));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Snapshot(action.SessionId));
        private static BrowserSnapshot Snapshot(string session) => new(42, session, "r1", "https://www.google.com/", "Google", "Search",
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
