using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// Ordinary ambiguous target selection (a song, a video, a product) is a <see cref="PendingChoice"/> when the user's own words
/// do not separate real candidates, and never a silent guess; when their words do separate them, it is just executed.
/// </summary>
public sealed class GenericChoiceTests
{
    private static readonly BrowserExecutionScope Active = new(BrowserScopeKind.ActiveTab, 1, "https://site.example/results", ExplicitSelection: true);

    private sealed class Compiler(params PlanStep[] steps) : IBrowserStepCompiler
    {
        public int Calls { get; private set; }
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult<CompiledBrowserTask?>(new(new InteractionPlan(utterance, utterance, steps),
                new BrowserGoalNormalization(utterance, null, null, null, null, [], utterance, [], SemanticEndState.StateChanged)));
        }
    }

    private sealed class Gateway(double operation, (string Ref, double P)[] bind) : IJevGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            Calls++;
            var top = bind.OrderByDescending(b => b.P).First();
            var probabilities = bind.ToDictionary(b => b.Ref, b => b.P);
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "CLICK", new Dictionary<string, double> { ["CLICK"] = operation, ["BLOCKED"] = 1 - operation }, operation),
                ["click_target"] = new("choice", top.Ref, probabilities, top.P),   // the action model is sure it should click the top one
                ["bind"] = new("choice", top.Ref, probabilities, top.P)
            });
        }
    }

    private sealed class Page(BrowserElement[] elements) : IChromeCompanionTransport
    {
        private string _url = "https://site.example/results";
        private int _n;
        public List<string> Acts { get; } = [];
        public bool IsConnected => true;
        private BrowserSnapshot Snap(string s, int t) => new(t, s, $"r{++_n}", _url, "T", "text", false, new(1280, 800, 0, 0, 6000), elements);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default)
        {
            Acts.Add($"{a.Action}:{a.ElementRef}");
            if (a.Action == "CLICK") _url = "https://site.example/watch/" + a.ElementRef;
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private static BrowserElement Video(string r, string name, string v, string by)
        => new(r, "link", name, true, false, null, $"https://site.example/watch?v={v}", new(0, 0, 10, 10, true), by);

    // "(Official Music Video)" contains a generic category word the compiler's description also used: it must not count as evidence.
    private static Page Results() => new([Video("e1", "Hello - Adele (Official Music Video)", "a", "Adele"), Video("e2", "Hello - Lionel Richie", "b", "Lionel Richie")]);

    private static (string, double)[] Close() => [("e1", .49), ("e2", .40), ("NONE", .06), ("e3", .05)];

    [Fact]
    public async Task AmbiguousVideoResults_CreateAChoice_EvenWhenTheClickOperationIsCertain_AndResumeWithoutRecompiling()
    {
        var page = Results();
        var gateway = new Gateway(.99, Close());
        var compiler = new Compiler(new PlanStep(PlanStepKind.Open, "Play a matching video", Target: "a matching video for \"hello\""));
        var service = new BrowserInteractionService(page, gateway, compiler);

        var result = await service.RunAsync("Play hello on YouTube, then stop", scope: Active);

        var pending = Assert.IsType<PendingChoice>(result.Pending);
        Assert.Empty(page.Acts);                                                       // CLICK=.99 is not "this is the item"
        Assert.Equal(["Hello - Adele (Official Music Video)", "Hello - Lionel Richie"], pending.Options.Select(o => o.DisplayText));
        Assert.Equal(ChoiceResolution.SatisfiesStep, pending.Resolution);

        var resumed = await service.ResumeChoiceAsync(pending.Options[1].ChoiceId);

        Assert.Equal(InteractionCompletionState.Complete, resumed.Completion);
        Assert.Equal(["CLICK:e2"], page.Acts);                                          // exactly the option picked
        Assert.Equal(1, compiler.Calls);                                                // same execution: no router, no compiler
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task AnArtistTheUserSaid_SettlesTheItem_SoNothingIsAsked()
    {
        var page = Results();
        var compiler = new Compiler(new PlanStep(PlanStepKind.Open, "Play Hello by Adele", Target: "Hello by Adele"));
        var service = new BrowserInteractionService(page, new Gateway(.99, Close()), compiler);

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Null(result.Pending);
        Assert.Equal(["CLICK:e1"], page.Acts);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public async Task AmbiguityNoHonestQuestionCanBeBuiltFrom_IsNotGuessedAt()
    {
        // Same words, same address shape, nothing to tell them apart: a binding problem, not a preference.
        var page = new Page([Video("e1", "Hello", "a", ""), Video("e2", "Hello", "b", "")]);
        var service = new BrowserInteractionService(page, new Gateway(.99, Close()),
            new Compiler(new PlanStep(PlanStepKind.Open, "Play Hello", Target: "Hello")));

        var result = await service.RunAsync("Play Hello, then stop", scope: Active);

        Assert.Null(result.Pending);
        Assert.Empty(page.Acts);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
    }

}
