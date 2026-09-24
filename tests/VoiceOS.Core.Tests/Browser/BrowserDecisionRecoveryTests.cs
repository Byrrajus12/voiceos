using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Logging;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserDecisionRecoveryTests
{
    [Fact]
    public async Task OneRequest_HasOperationAndOnlyCompatibleTargetHeadsOverOneState()
    {
        var page = Page("https://search.test/", Text("q"), Click("result"), Scroll("down"));
        var gateway = new FakeGateway((state, questions) =>
        {
            Assert.Equal("https://search.test/", Evidence(state).GetProperty("current_url").GetString());
            Assert.Equal(["operation", "goal_achieved", "stuck", "text_target", "click_target"],
                questions.Keys.ToArray());
            Assert.Equal(["q"], questions["text_target"].Criteria!.Keys);
            Assert.Equal(["result"], questions["click_target"].Criteria!.Keys);
            Assert.Contains("SCROLL_DOWN", questions["operation"].Criteria!.Keys);
            Assert.DoesNotContain("action", questions.Keys);
            return Answers(("operation", Choice("CLICK")), ("click_target", Choice("result")));
        });
        var decision = await Decide(new(gateway, Goal()), page);
        Assert.Equal(InteractionActionKind.Activate, decision.Action?.Kind);
        Assert.Equal("result", decision.Action?.TargetId);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task ClickCannotConsumeTextHead_AndTextCannotConsumeClickHead()
    {
        var page = Page("https://search.test/", Text("q"), Click("result"));
        var click = new FakeGateway((_, _) => Answers(("operation", Choice("CLICK")),
            ("click_target", Choice("q")), ("text_target", Choice("q"))));
        Assert.Null((await Decide(new(click, Goal()), page)).Action);
        Assert.Equal(2, click.Calls);

        var type = new FakeGateway((_, _) => Answers(("operation", Choice("TYPE_TEXT")),
            ("text_target", Choice("result")), ("click_target", Choice("result"))));
        Assert.Null((await Decide(new(type, Goal()), page)).Action);
        Assert.Equal(2, type.Calls);
    }

    [Theory]
    [InlineData("SCROLL_DOWN", "down")]
    [InlineData("SCROLL_UP", "up")]
    public async Task ScrollNeedsNoTargetHead(string operation, string direction)
    {
        var page = Page("https://search.test/", Scroll("down"), Scroll("up"));
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.DoesNotContain("click_target", questions);
            Assert.DoesNotContain("text_target", questions);
            return Answers(("operation", Choice(operation)));
        });
        var decision = await Decide(new(gateway, Goal()), page);
        Assert.Equal(InteractionActionKind.Scroll, decision.Action?.Kind);
        Assert.Equal(direction, decision.Action?.Direction);
        Assert.Null(decision.Action?.TargetId);
    }

    [Fact]
    public async Task GoalAchievedIsIndependentOfOperation_WeakDoneIsReconsidered()
    {
        var page = Page("https://search.test/results", Click("result"));
        var gateway = new FakeGateway((state, questions) =>
        {
            Assert.Contains("goal_achieved", questions);
            Assert.Contains("stuck", questions);
            return state.GetProperty("correction").ValueKind == JsonValueKind.Null
                ? Answers(("operation", Choice("DONE")), ("goal_achieved", Noul(.1)))
                : Answers(("operation", Choice("CLICK")), ("click_target", Choice("result")),
                    ("goal_achieved", Noul(.1)));
        });
        var decision = await Decide(new(gateway, Goal()), page);
        Assert.Equal("result", decision.Action?.TargetId);
        Assert.Equal(2, gateway.Calls);
    }

    [Fact]
    public async Task WeakBlockedIsReconsideredAgainstIndependentStuckHead()
    {
        var page = Page("https://search.test/results", Click("result"));
        var gateway = new FakeGateway((state, _) =>
            state.GetProperty("correction").ValueKind == JsonValueKind.Null
                ? Answers(("operation", Choice("BLOCKED")), ("stuck", Noul(.1)))
                : Answers(("operation", Choice("CLICK")), ("click_target", Choice("result")),
                    ("stuck", Noul(.1))));
        var decision = await Decide(new(gateway, Goal()), page);
        Assert.Equal("result", decision.Action?.TargetId);
        Assert.Equal(2, gateway.Calls);
    }

    [Fact]
    public async Task StrongDoneOnRepository_RequiresFreshIndependentConfirmation()
    {
        var goal = BrowserGoal.FromUtterance("Find and open the official React repository.") with
        {
            Normalization = new("Open the official React repository", "React", "repository", "GitHub",
                "https://github.com/", ["React GitHub"], "Official React repository is open", [])
        };
        var page = Page("https://github.com/react/react", title: "facebook/react: The library for web and native user interfaces",
            visibleText: "React repository Code Issues Pull requests");
        var gateway = new FakeGateway((state, questions) =>
        {
            Assert.Equal("https://github.com/react/react", Evidence(state).GetProperty("current_url").GetString());
            return questions.ContainsKey("operation")
                ? Answers(("operation", Choice("DONE")), ("goal_achieved", Noul(.95)))
                : Answers(("goal_achieved", Noul(.96)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var surface = new SequenceSurface(source, page, page with { Revision = 2 });
        var result = await new InteractionEngine().RunAsync(new(goal.OriginalUtterance), surface, source);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, surface.Observations);
        Assert.Equal(2, gateway.Calls);
    }

    [Fact]
    public async Task FreshObservationCanRejectProvisionalDone()
    {
        var goal = Goal();
        var page = Page("https://search.test/results");
        var gateway = new FakeGateway((_, questions) => questions.ContainsKey("operation")
            ? Answers(("operation", Choice("DONE")), ("goal_achieved", Noul(.95)))
            : Answers(("goal_achieved", Noul(.1))));
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        var result = await new InteractionEngine().RunAsync(new(goal.OriginalUtterance),
            new SequenceSurface(source, page), source, new(1, 1));
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, gateway.Calls);
    }

    [Fact]
    public async Task HistoryContainsVisibleEffectAndUrls_AndStuckCanValidateBlock()
    {
        var page = Page("https://search.test/results", Click("result"));
        var action = page.Candidates.Single().Actions.Single();
        var prior = Page("https://search.test/", Click("result"));
        var history = new[]
        {
            new InteractionHistoryEntry(prior.StateKey, action,
                InteractionActionResult.Ok(), page.StateKey, false, DateTimeOffset.UtcNow,
                prior.Evidence, page.Evidence),
            new InteractionHistoryEntry(page.StateKey, action,
                InteractionActionResult.Fail(InteractionResultStatus.NoEffect, "unchanged"),
                page.StateKey, false, DateTimeOffset.UtcNow, page.Evidence, page.Evidence),
            new InteractionHistoryEntry(page.StateKey, action,
                InteractionActionResult.Fail(InteractionResultStatus.NoEffect, "unchanged again"),
                page.StateKey, true, DateTimeOffset.UtcNow, page.Evidence, page.Evidence)
        };
        var gateway = new FakeGateway((state, questions) =>
        {
            var effects = state.GetProperty("recent_actions_and_effects");
            Assert.Equal(3, effects.GetArrayLength());
            Assert.Contains("navigated from", effects[0].GetProperty("visible_effect").GetString());
            Assert.Equal("no visible change", effects[1].GetProperty("visible_effect").GetString());
            Assert.Equal("no visible change", effects[2].GetProperty("visible_effect").GetString());
            Assert.True(effects[2].GetProperty("Suppressed").GetBoolean());
            Assert.Contains("https://search.test/results", state.GetProperty("recent_visited_urls").ToString());
            Assert.Contains("stuck", questions);
            return Answers(("operation", Choice("BLOCKED")), ("stuck", Noul(.95)));
        });
        var source = new TypeSafeBrowserDecisionSource(gateway, Goal());
        var decision = await source.DecideAsync(new(new("goal"), page, history, new(), new(2, 2, 1, 1)));
        Assert.Equal(InteractionCompletionState.Uncertain, decision.Completion);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task TextValueIsResolvedOnlyAfterFieldSelection()
    {
        var resolver = new CapturingResolver();
        var page = Page("https://search.test/", Text("q"), Text("address"));
        var gateway = new FakeGateway((_, questions) =>
        {
            Assert.Equal(2, questions["text_target"].Criteria!.Count);
            Assert.DoesNotContain(questions["operation"].Criteria!.Values, value => value.Contains("shawarma"));
            return Answers(("operation", Choice("TYPE_TEXT")), ("text_target", Choice("q")));
        });
        var decision = await Decide(new(gateway, Goal(), textValues: resolver), page);
        Assert.Equal("q", resolver.SelectedField);
        Assert.Equal("chicken shawarma", decision.Action?.Text);
    }

    [Fact]
    public async Task GenericTextGroundingUsesDiscoveryQueryThenEntityOnPreferredHost()
    {
        var goal = BrowserGoal.FromUtterance("Find chicken shawarma on a delivery service") with
        {
            Normalization = new("Find chicken shawarma on the delivery service", "chicken shawarma", "food",
                "Delivery Service", "https://delivery.test/", ["chicken shawarma Delivery Service"],
                "Matching food is visible", [])
        };
        var resolver = new GroundedBrowserTextValueResolver();
        Assert.Equal("chicken shawarma Delivery Service", await resolver.ResolveAsync(
            new(goal, Text("q"), Page("https://search.test/", Text("q")))));
        Assert.Equal("chicken shawarma", await resolver.ResolveAsync(
            new(goal, Text("q"), Page("https://delivery.test/search", Text("q")))));
        Assert.Null(await resolver.ResolveAsync(new(goal,
            new("address", "textbox 'Delivery address' value=''", [new("text-address", InteractionActionKind.SetText, "address")]),
            Page("https://delivery.test/"))));
    }

    [Fact]
    public async Task ContextualTextHelperGetsSelectedFieldAndReturnsOneBoundedValue()
    {
        var handler = new TextHelperHandler();
        var resolver = new OpenRouterBrowserTextValueResolver(new HttpClient(handler), "test-key");
        var field = new InteractionCandidate("departure", "textbox 'Departure city' value='' context='Flight search'",
            [new("type-departure", InteractionActionKind.SetText, "departure")]);
        var value = await resolver.ResolveAsync(new(BrowserGoal.FromUtterance("Find flights from Zurich to London"),
            field, Page("https://travel.test/", field, Click("irrelevant"))));
        Assert.Equal("Zurich", value);
        Assert.Equal(1, handler.Calls);
    }

    private static BrowserGoal Goal() => BrowserGoal.FromUtterance("Find chicken shawarma on a delivery service");
    private static InteractionCandidate Click(string id) => new(id, $"link '{id}'",
        [new($"click-{id}", InteractionActionKind.Activate, id)]);
    private static InteractionCandidate Text(string id) => new(id, $"searchbox '{id}' value=''",
        [new($"text-{id}", InteractionActionKind.SetText, id)]);
    private static InteractionCandidate Scroll(string direction) => new($"page-{direction}", "Current page",
        [new($"scroll-{direction}", InteractionActionKind.Scroll, Direction: direction)]);
    private static InteractionObservation Page(string url, InteractionCandidate[]? candidates = null,
        string title = "Page", string visibleText = "Visible page")
        => new(1, url, JsonSerializer.Serialize(new { current_url = url, current_title = title,
            visible_text = visibleText }), candidates ?? []);
    private static InteractionObservation Page(string url, params InteractionCandidate[] candidates)
        => Page(url, candidates, "Page", "Visible page");
    private static JsonElement Evidence(JsonElement state)
        => JsonDocument.Parse(state.GetProperty("fresh_dom_observation").GetString()!).RootElement.Clone();
    private static async Task<InteractionDecision> Decide(TypeSafeBrowserDecisionSource source, InteractionObservation page)
        => await source.DecideAsync(new(new("goal"), page, [], new(), new(0, 0, 0, 0)));
    private static JevAnswer Choice(string value) => new("choice", value,
        new Dictionary<string, double> { [value] = .99 }, .99);
    private static JevAnswer Noul(double value) => new("noul", value >= .5 ? "true" : "false",
        new Dictionary<string, double> { ["noul"] = value }, value);
    private static IReadOnlyDictionary<string, JevAnswer> Answers(params (string Key, JevAnswer Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    private sealed class CapturingResolver : IBrowserTextValueResolver
    {
        public string? SelectedField { get; private set; }
        public ValueTask<string?> ResolveAsync(BrowserTextValueRequest request, CancellationToken cancellationToken = default)
        {
            SelectedField = request.Field.Id;
            return ValueTask.FromResult<string?>("chicken shawarma");
        }
    }
    private sealed class TextHelperHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Departure city", body);
            Assert.DoesNotContain("irrelevant", body);
            Assert.Contains("browser_field_value", body);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"text\":\"Zurich\"}"}}]}""")
            };
        }
    }
    private sealed class FakeGateway(Func<JsonElement, IReadOnlyDictionary<string, JevQuestionDto>,
        IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(JsonSerializer.SerializeToElement(state), questions));
        }
    }
    private sealed class SequenceSurface(TypeSafeBrowserDecisionSource completion,
        params InteractionObservation[] observations) : IInteractionSurface
    {
        public int Observations { get; private set; }
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(observations[Math.Min(Observations++, observations.Length - 1)]);
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action,
            InteractionObservation observation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No execution expected");
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal,
            InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> recentHistory,
            CancellationToken cancellationToken = default)
            => completion.AssessAsync(BrowserGoal.FromUtterance(goal.Text), observation, recentHistory, cancellationToken);
    }
}
