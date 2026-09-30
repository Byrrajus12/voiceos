using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserStepRunnerTests
{
    private static BrowserElement El(string r, string role, string name, bool editable = false, string? href = null,
        string? value = null, bool search = false, string? form = null, bool submit = false, bool inList = false)
        => new(r, role, name, true, editable, value, href, new(0, 0, 100, 20, true), "", search, form, submit,
            form is null ? null : "/search", null, inList, null);

    private static readonly BrowserElement[] Home =
    [
        El("e1", "searchbox", "Search GitHub", editable: true, search: true, form: "f1"),
        El("e2", "button", "Search", form: "f1", submit: true)
    ];

    private static readonly BrowserElement[] Results =
    [
        El("e1", "link", "tauri-apps/tauri", href: "https://github.com/tauri-apps/tauri", inList: true),
        El("e2", "link", "slint-ui/slint", href: "https://github.com/slint-ui/slint", inList: true)
    ];

    /// <summary>A tiny scripted site: home with a search form, a results page, and the repository page.</summary>
    private sealed class Site : IChromeCompanionTransport
    {
        private string _page = "home";
        private string _typed = "";
        public List<string> Actions { get; } = [];
        private int _n;

        private BrowserSnapshot Snap(string session, int tab)
        {
            _n++;
            return _page switch
            {
                "results" => new(tab, session, $"r{_n}", "https://github.com/search?q=Rust+GUI+framework", "Search", "results", false,
                    new(1280, 800, 0, 0), Results, true),
                "repo" => new(tab, session, $"r{_n}", "https://github.com/tauri-apps/tauri", "tauri-apps/tauri", "Tauri", false,
                    new(1280, 800, 0, 0), [], true),
                _ => new(tab, session, $"r{_n}", "https://github.com/", "GitHub", "home", false, new(1280, 800, 0, 0),
                    Home.Select(e => e.Ref == "e1" ? e with { Value = _typed } : e).ToArray(), false)
            };
        }

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken ct = default)
            => ValueTask.FromResult(Snap(sessionId, 5));
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken ct = default)
            => ValueTask.FromResult(Snap(sessionId, tabId));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken ct = default)
        {
            Actions.Add($"{a.Action}:{a.ElementRef}:{a.Text}");
            if (a.Action == "REPLACE_TEXT") _typed = a.Text ?? "";
            else if (a.Action == "CLICK" && _page == "home" && a.ElementRef == "e2") _page = "results";
            else if (a.Action == "CLICK" && _page == "results" && a.ElementRef == "e1") _page = "repo";
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private sealed class Compiler(string json) : IBrowserStepCompiler
    {
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
            => ValueTask.FromResult(OpenRouterBrowserStepCompiler.Parse(json, utterance));
    }

    private sealed class Gateway(Func<JsonElement, IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public List<JsonElement> States { get; } = [];
        public List<string[]> Heads { get; } = [];
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken ct = default)
        {
            var element = JsonSerializer.SerializeToElement(state);
            States.Add(element);
            Heads.Add(questions.Keys.ToArray());
            return Task.FromResult(answer(element, questions));
        }
    }

    private static JevAnswer Choice(string pick, params string[] others)
        => new("choice", pick, others.ToDictionary(static o => o, static _ => .03).Append(new(pick, .94)).ToDictionary(), .94);

    private static string PickTauri(IReadOnlyDictionary<string, JevQuestionDto> questions, string head)
        => questions[head].Criteria!.First(c => c.Value.Contains("tauri-apps/tauri")).Key;

    private const string Plan = """
        {"finalGoal":"Open the Tauri repository","endState":"ResourceOpened","resourceType":"repository","preferredService":"GitHub",
         "preferredServiceUrl":"https://github.com/",
         "steps":[
          {"kind":"Reach","description":"Go to GitHub","query":null,"target":"GitHub","progress":"Opening GitHub"},
          {"kind":"Search","description":"Search GitHub for Rust GUI framework","query":"Rust GUI framework","target":null,"progress":"Searching GitHub"},
          {"kind":"Locate","description":"Find the Tauri repository","query":null,"target":"Tauri repository","progress":"Looking for Tauri"},
          {"kind":"Open","description":"Open the Tauri repository","query":null,"target":"Tauri repository","progress":"Opening Tauri"}],
         "correctedTerms":[]}
        """;

    private static Gateway TauriGateway() => new((_, q) => new Dictionary<string, JevAnswer>
    {
        ["operation"] = Choice("CLICK", "SCROLL_DOWN"),
        ["click_target"] = Choice(PickTauri(q, "click_target"), "e2"),
        ["bind"] = Choice(PickTauri(q, "bind"), "NONE")
    });

    private static BrowserExecutionScope NewGitHubTab => new(BrowserScopeKind.NewTaskTab, Destination: new Uri("https://github.com/"));

    [Fact]
    public async Task FourStepPlan_RunsEachStepInOrder_ToCompletionWithoutWholeGoalConfirmation()
    {
        var site = new Site();
        var gateway = TauriGateway();
        var service = new BrowserInteractionService(site, gateway, compiler: new Compiler(Plan), proofMode: ProofMode.On);

        var outcome = await service.RunAsync("Go to GitHub, search for a Rust GUI framework and open the Tauri repository.",
            scope: NewGitHubTab);

        Assert.Equal(InteractionCompletionState.Complete, outcome.Completion);
        Assert.Equal("https://github.com/tauri-apps/tauri", outcome.Url);
        // Reach is already satisfied, the query is typed and submitted by code (no model call), Locate is satisfied by the
        // results, and only the Open step needed one model decision.
        Assert.Equal(["REPLACE_TEXT:e1:Rust GUI framework", "CLICK:e2:", "CLICK:e1:"], site.Actions);
        var decision = Assert.Single(gateway.States);
        Assert.DoesNotContain(gateway.Heads, heads => heads.Contains("goal_achieved") || heads.Contains("stuck"));
        // That one decision saw the current step, what was already done, and the preserved original request.
        Assert.Equal("Open", decision.GetProperty("current_step").GetProperty("kind").GetString());
        Assert.Equal(3, decision.GetProperty("completed_steps").GetArrayLength());
        Assert.Contains("Tauri", decision.GetProperty("original_goal").GetString());
    }

    [Fact]
    public async Task OpenStep_CompletesFromTheBoundActionAndItsEffect_NotFromAnotherModelJudgment()
    {
        var site = new Site();
        var gateway = TauriGateway();
        var service = new BrowserInteractionService(site, gateway, compiler: new Compiler(Plan), proofMode: ProofMode.On);
        await service.RunAsync("open Tauri", scope: NewGitHubTab);
        // The last action is the bound click; nothing asked the model whether the destination looks right.
        Assert.Equal("CLICK:e1:", site.Actions[^1]);
        Assert.All(gateway.Heads, heads => Assert.DoesNotContain("goal_achieved", heads));
    }

    // -- postconditions -------------------------------------------------------------------

    private static InteractionObservation Obs(string url, params BrowserElement[] elements)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            current_url = url, current_title = "t", visible_text = "",
            elements = elements.Select(e => new { id = e.Ref, Role = e.Role, Name = e.Name, Href = e.Href, Enabled = true })
        });
        return new(1, "k", evidence, []);
    }

    private static Effect Fx(EffectKind kind, string action, Dictionary<string, string>? data = null, TypedRef? subject = null)
        => new(kind, EffectSource.CompanionResponse, EffectStrength.Observed, subject, data) { Id = $"s.{kind}", ActionId = action };

    private static PlannedStepEvaluator Evaluator() => new(new BrowserGoal("x") with
    {
        Normalization = new("g", null, null, "GitHub", "https://github.com/", [], "g", [])
    }, new BrowserProofEvaluator(new BrowserGoal("x")));

    [Fact]
    public void Search_NeedsTheQueryInTheField_ASubmitAndAChangedPage()
    {
        var step = new OutcomeStep("s2", ProofFamily.Find, new("Search GitHub", "Rust GUI framework"), WithinPlan: true);
        var typed = Fx(EffectKind.TextSet, "a1", new() { ["value"] = "Rust GUI framework", ["matched"] = "true" });
        var submit = new InteractionAction("a2", InteractionActionKind.Activate, "e2");
        var obs = Obs("https://github.com/search?q=Rust");
        var evaluator = Evaluator();
        // Typed but not yet submitted.
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [typed], new("a1", InteractionActionKind.SetText, "e1"), null, obs)).Status);
        // Submitted but the page did not change.
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [typed, Fx(EffectKind.NoEffect, "a2")], submit, null, obs)).Status);
        // Submitted and navigated.
        var nav = Fx(EffectKind.Navigated, "a2", new() { ["to"] = "https://github.com/search?q=Rust" });
        Assert.Equal(ProofStatus.Proved, evaluator.Evaluate(new(step, [typed, nav], submit, null, obs)).Status);
        // A different query in the field never proves this step.
        var other = Fx(EffectKind.TextSet, "a1", new() { ["value"] = "something else", ["matched"] = "true" });
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [other, nav], submit, null, obs)).Status);
    }

    [Fact]
    public void Locate_IsSatisfiedByThePresenceOfTheTarget_AndByNothingElse()
    {
        var step = new OutcomeStep("s3", ProofFamily.Locate, new("Tauri repository"), WithinPlan: true);
        var evaluator = Evaluator();
        Assert.Equal(ProofStatus.Proved, evaluator.Evaluate(new(step, [], null, null, Obs("https://x/", Results))).Status);
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [], null, null,
            Obs("https://x/", El("e1", "link", "slint-ui/slint")))).Status);
    }

    [Fact]
    public void Open_WithoutABinding_OrWithoutAConsequence_IsNotComplete()
    {
        var step = new OutcomeStep("s4", ProofFamily.Activate, new("Tauri repository"), WithinPlan: true);
        var click = new InteractionAction("a1", InteractionActionKind.Activate, "e1");
        var subject = new TypedRef(5, "s", 1, "e1", "fp", "tauri", "link");
        var activated = Fx(EffectKind.Activated, "a1", subject: subject);
        var nav = Fx(EffectKind.Navigated, "a1", new() { ["to"] = "https://github.com/tauri-apps/tauri" }, subject);
        var binding = new TargetBinding("e1", 1, BindingMethod.JevChoice, 2, .9, .8);
        var evaluator = Evaluator();
        var obs = Obs("https://github.com/tauri-apps/tauri");
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [activated, nav], click, null, obs)).Status);
        Assert.Equal(ProofStatus.NotYet, evaluator.Evaluate(new(step, [activated], click, binding, obs)).Status);
        Assert.Equal(ProofStatus.Proved, evaluator.Evaluate(new(step, [activated, nav], click, binding, obs)).Status);
        // A different element than the one bound is a binding failure, never proof.
        var other = binding with { ElementRef = "e2" };
        Assert.Equal(ProofStatus.Inconclusive, evaluator.Evaluate(new(step, [activated, nav], click, other, obs)).Status);
    }
}
