using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>Target ambiguity is resolved BEFORE the item-selecting action: a question never follows a click that already chose.</summary>
public sealed class AskBeforeActTests
{
    private static readonly BrowserExecutionScope Active = new(BrowserScopeKind.ActiveTab, 1, "https://site.example/results", ExplicitSelection: true);

    private sealed class Compiler(PlanStep step) : IBrowserStepCompiler
    {
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
            => ValueTask.FromResult<CompiledBrowserTask?>(new(new InteractionPlan(utterance, utterance, [step]),
                new BrowserGoalNormalization(utterance, null, null, null, null, [], utterance, [], SemanticEndState.StateChanged)));
    }

    /// <summary>The binder finds nothing confident (NONE) while the operation head is sure it should click <paramref name="clickTop"/>.</summary>
    private sealed class Gateway(string clickTop, double clickP = .55) : IJevGateway
    {
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            var labels = q["click_target"].Criteria!.Keys.ToArray();
            var click = labels.ToDictionary(l => l, l => l == clickTop ? clickP : (1 - clickP) / Math.Max(1, labels.Length - 1));
            var bind = q["bind"].Criteria!.Keys.ToDictionary(l => l, l => l == "NONE" ? .8 : .2 / Math.Max(1, labels.Length));
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", "CLICK", new Dictionary<string, double> { ["CLICK"] = .99, ["BLOCKED"] = .01 }, .99),
                ["click_target"] = new("choice", clickTop, click, clickP),
                ["bind"] = new("choice", "NONE", bind, .8)
            });
        }
    }

    private sealed class Page(bool needsScroll = false) : IChromeCompanionTransport
    {
        private bool _scrolled;
        private int _n;
        private string _url = "https://site.example/results";
        public List<string> Acts { get; } = [];
        public bool IsConnected => true;
        private static BrowserElement Video(string r, string name, string v, string by)
            => new(r, "link", name, true, false, null, $"https://site.example/watch?v={v}", new(0, 0, 10, 10, true), by);
        private BrowserSnapshot Snap(string s, int t) => new(t, s, $"r{++_n}", _url, "T", "text", false, new(1280, 800, 0, _scrolled ? 600 : 0, needsScroll ? 3000 : 800),
            needsScroll && !_scrolled
                ? [new("n1", "link", "Sign in", true, false, null, "https://site.example/login", new(0, 0, 10, 10, true), ""), new("n2", "link", "Shorts", true, false, null, "https://site.example/shorts", new(0, 0, 10, 10, true), "")]
                : [Video("e1", "Hello - Adele (Official Video)", "a", "Adele"), Video("e2", "Hello - Lionel Richie", "b", "Lionel Richie")]);
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default)
        {
            Acts.Add($"{a.Action}:{a.ElementRef}");
            if (a.Action == "SCROLL") _scrolled = true;
            if (a.Action == "CLICK") _url = "https://site.example/watch/" + a.ElementRef;
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    [Theory]
    [InlineData(PlanStepKind.Open)]
    [InlineData(PlanStepKind.Act)]
    public async Task AmbiguousItemSelection_AsksBeforeAnyTargetChangingAction(PlanStepKind kind)
    {
        var page = new Page();
        var step = new PlanStep(kind, "Choose a matching video", Target: kind == PlanStepKind.Open ? "a video for \"hello\"" : null);
        var service = new BrowserInteractionService(page, new Gateway("e1"), new Compiler(step));

        var result = await service.RunAsync("Play hello, then stop", scope: Active);

        Assert.IsType<PendingChoice>(result.Pending);
        Assert.Empty(page.Acts);                              // nothing was clicked on the user's behalf first
    }

    [Fact]
    public async Task AGroundedWinner_IsTheExactCandidateThatExecutes_EvenWhenTheOperationHeadPointedElsewhere()
    {
        var page = new Page();
        var service = new BrowserInteractionService(page, new Gateway("e2"),   // the click head leans to Lionel Richie
            new Compiler(new PlanStep(PlanStepKind.Open, "Play Hello by Adele", Target: "Hello by Adele")));

        var result = await service.RunAsync("Play Hello by Adele, then stop", scope: Active);

        Assert.Null(result.Pending);
        Assert.Equal(["CLICK:e1"], page.Acts);                // the user said Adele: that result, exactly
    }

    [Fact]
    public async Task RevealingScrollsStillRunFirst_ThenOnceSeveralCandidatesExistNoneIsClickedBeforeTheQuestion()
    {
        var page = new Page(needsScroll: true);
        var service = new BrowserInteractionService(page, new Gateway("e1"),
            new Compiler(new PlanStep(PlanStepKind.Open, "Play a matching video", Target: "a video for \"hello\"")));

        var result = await service.RunAsync("Play hello, then stop", scope: Active);

        Assert.IsType<PendingChoice>(result.Pending);
        Assert.NotEmpty(page.Acts);
        Assert.All(page.Acts, a => Assert.StartsWith("SCROLL", a));   // only revealing scrolls; no click
    }
}
