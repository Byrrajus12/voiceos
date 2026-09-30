using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class PlanDataflowTests
{
    private const string Plan = """
        {"finalGoal":"Find a figurine","endState":"ResultsVisible","resourceType":null,"preferredService":null,"preferredServiceUrl":null,"correctedTerms":[],
         "steps":[{"kind":"Search","description":"Search rankings","query":"series character popularity","target":null,"progress":null,"searchScope":"Global","produces":null},
                  {"kind":"Locate","description":"Identify the character ranked second","query":null,"target":"the character ranked second","progress":null,"searchScope":null,"produces":"character"},
                  {"kind":"Search","description":"Search figurines of ${character}","query":"${character} figurine","target":null,"progress":"Searching ${character}","searchScope":"Global","produces":null}]}
        """;

    [Fact]
    public void ProducerAndConsumer_ParseAndResolveAgainstCapturedOutput()
    {
        var plan = OpenRouterBrowserStepCompiler.Parse(Plan, "x")!.Plan;
        Assert.Equal("character", plan.Steps[1].Produces);
        Assert.True((plan with { CurrentStepIndex = 1 }).IsConsumed("character"));
        var at = (plan with { CurrentStepIndex = 2 });
        Assert.Equal("character", at.ResolveCurrent().Missing);
        var (resolved, missing) = at.WithOutput("character", "Zoro").ResolveCurrent();
        Assert.Null(missing);
        Assert.Equal("Zoro figurine", resolved.Current.Query);
        Assert.Equal("Searching Zoro", resolved.Current.Progress);
    }

    [Fact]
    public void ReferenceToAnUnproducedValue_IsRejectedAtCompile()
        => Assert.Null(OpenRouterBrowserStepCompiler.Parse(Plan.Replace("\"produces\":\"character\"", "\"produces\":null"), "x"));

    [Theory]
    [InlineData("page 7 control", "page 7", true)]
    [InlineData("the third search result", "the third search result", true)]
    [InlineData("page 7", "the Jim Lovell page", false)]
    public void LocateBeforeOpenOnTheSameTarget_Collapses(string locate, string open, bool collapsed)
    {
        var steps = OpenRouterBrowserStepCompiler.CollapseRedundantLocates([
            new(PlanStepKind.Locate, "Locate " + locate, Target: locate), new(PlanStepKind.Open, "Open " + open, Target: open)]);
        Assert.Equal(collapsed ? 1 : 2, steps.Count);
        Assert.Equal(PlanStepKind.Open, steps[^1].Kind);
    }

    [Fact]
    public void ProducingLocate_IsNeverCollapsed()
    {
        var steps = OpenRouterBrowserStepCompiler.CollapseRedundantLocates([
            new(PlanStepKind.Locate, "Find it", Target: "page 7", Produces: "p"), new(PlanStepKind.Open, "Open page 7", Target: "page 7")]);
        Assert.Equal(2, steps.Count);
    }

    [Fact]
    public void ValueMustAppearOnThePage()
    {
        var page = new BrowserSnapshot(1, "s", "r", "https://x.example/", "T", "Top list 1. Luffy 2. Zoro 3. Nami", false, new(1, 1, 0, 0), []);
        Assert.Equal("Zoro", ValueGrounding.Validate(" \"zoro\" ", page));
        Assert.Null(ValueGrounding.Validate("Sanji", page));
    }

    private sealed class Extractor(string? value) : IBrowserValueExtractor
    {
        public int Calls { get; private set; }
        public ValueTask<string?> ExtractAsync(ValueRequest request, CancellationToken ct = default) { Calls++; return ValueTask.FromResult(value); }
    }

    private sealed class Compiler : IBrowserStepCompiler
    {
        public ValueTask<CompiledBrowserTask?> CompileAsync(string u, CancellationToken ct = default)
            => ValueTask.FromResult(OpenRouterBrowserStepCompiler.Parse("""
                {"finalGoal":"g","endState":"OtherBoundedGoal","resourceType":null,"preferredService":null,"preferredServiceUrl":null,"correctedTerms":[],
                 "steps":[{"kind":"Locate","description":"Identify the topic","query":null,"target":"the topic","progress":null,"searchScope":null,"produces":"topic"},
                          {"kind":"Act","description":"Do something with ${topic}","query":null,"target":null,"progress":null,"searchScope":null,"produces":null}]}
                """, u));
    }

    private sealed class Gateway : IJevGateway
    {
        public List<string> Steps { get; } = [];
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            Steps.Add(JsonSerializer.SerializeToElement(state).GetProperty("current_step").GetProperty("description").GetString()!);
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
                { ["operation"] = new("choice", "BLOCKED", new Dictionary<string, double> { ["BLOCKED"] = .9 }, .9) });
        }
    }

    private sealed class Page : IChromeCompanionTransport
    {
        public bool IsConnected => true;
        private static BrowserSnapshot Snap(string s, int t) => new(t, s, "r1", "https://x.example/", "T", "topic Alpha is ranked second", false, new(1, 1, 0, 0), []);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default) => ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
    }

    private static readonly BrowserExecutionScope Scope = new(BrowserScopeKind.ActiveTab, 1, "https://x.example/", ExplicitSelection: true);

    [Fact]
    public async Task CapturedValue_FeedsTheNextStep_WithoutAModelCallForTheProducer()
    {
        var gateway = new Gateway();
        var extractor = new Extractor("Alpha");
        var result = await new BrowserInteractionService(new Page(), gateway, new Compiler(), extractor: extractor)
            .RunAsync("find the topic, then act on it", scope: Scope);
        Assert.Equal(1, extractor.Calls);
        Assert.Contains("Do something with Alpha", gateway.Steps);
        Assert.DoesNotContain(gateway.Steps, s => s.Contains("${"));
    }

    [Fact]
    public async Task UnresolvedProducer_FailsMeaningfully_AndNeverRunsTheConsumer()
    {
        var gateway = new Gateway();
        var result = await new BrowserInteractionService(new Page(), gateway, new Compiler(), extractor: new Extractor("Not on the page"))
            .RunAsync("find the topic, then act on it", scope: Scope);
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal("I looked, but couldn't determine the topic.", result.Detail);
        Assert.Empty(gateway.Steps);
    }
}
