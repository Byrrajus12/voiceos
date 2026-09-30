using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ActSafetyTests
{
    private sealed class Compiler(PlanStepKind kind, string description) : IBrowserStepCompiler
    {
        public ValueTask<CompiledBrowserTask?> CompileAsync(string utterance, CancellationToken ct = default)
            => ValueTask.FromResult<CompiledBrowserTask?>(new(new InteractionPlan(utterance, description,
                [new(kind, description, Target: kind == PlanStepKind.Open ? description : null)]),
                new BrowserGoalNormalization(description, null, null, null, null, [], description, [], SemanticEndState.StateChanged)));
    }

    private sealed class Page : IChromeCompanionTransport
    {
        private int _n;
        private int _scrollY;
        private string _url = "https://x.example/start";
        public List<string> Acts { get; } = [];
        public bool IsConnected => true;

        private BrowserSnapshot Snap(string session, int tab) => new(tab, session, $"r{++_n}", _url, "T", "text", false,
            new(1280, 800, 0, _scrollY, 20_000),
            [new("e1", "link", "Home", true, false, null, "https://x.example/", new(0, 0, 10, 10, true), ""),
             new("e2", "button", "Download", true, false, null, null, new(0, 0, 10, 10, true), ""),
             // Scrolling brings different content into view.
             new("e3", "link", $"Item {_scrollY}", true, false, null, null, new(0, 0, 10, 10, true), "")]);

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default)
        {
            Acts.Add(a.Action + ":" + a.ElementRef);
            if (a.Action == "SCROLL") _scrollY += 700;
            if (a.Action == "CLICK") _url = "https://x.example/elsewhere-" + a.ElementRef;   // any click changes the page
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private sealed class Gateway(Func<IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer(q));
        }
    }

    private static JevAnswer Pick(string choice, double p, string other)
        => new("choice", choice, new Dictionary<string, double> { [choice] = p, [other] = 1 - p }, p);

    private static Gateway ClickGateway(string target, double p, string other) => new(_ => new Dictionary<string, JevAnswer>
    {
        ["operation"] = Pick("CLICK", .95, "BLOCKED"), ["click_target"] = Pick(target, .95, other), ["bind"] = Pick(target, p, other)
    });

    private static readonly BrowserExecutionScope Scope = new(BrowserScopeKind.ActiveTab, 1, "https://x.example/start", ExplicitSelection: true);

    [Fact]
    public async Task ActClick_OnAWeakOrWrongTarget_IsNotSuccessJustBecauseThePageChanged()
    {
        var page = new Page();
        // The binder leans to "Home" (p=.55, margin .1) although the step asks for the Download button.
        var service = new BrowserInteractionService(page, ClickGateway("e1", .55, "e2"), new Compiler(PlanStepKind.Act, "Click the Download button"));
        var result = await service.RunAsync("click the download button, then stop", scope: Scope);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
        Assert.NotEqual(InteractionCompletionState.Uncertain, result.Completion);
        Assert.DoesNotContain(page.Acts, a => a.StartsWith("CLICK"));
        Assert.Equal("I couldn't find a clear match for that on this page.", result.Detail);
    }

    [Fact]
    public async Task ActClick_OnAnEstablishedTarget_SucceedsWithItsEffect()
    {
        var page = new Page();
        var service = new BrowserInteractionService(page, ClickGateway("e2", .95, "e1"), new Compiler(PlanStepKind.Act, "Click the Download button"));
        var result = await service.RunAsync("click the download button, then stop", scope: Scope);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["CLICK:e2"], page.Acts);
    }

    [Fact]
    public async Task OpenWithNoPlausibleTargetOnScreen_ScrollsInCodeWithoutAModelCallPerScroll()
    {
        var page = new Page();
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["bind"] = Pick("NONE", .9, "e1"), ["operation"] = Pick("BLOCKED", .9, "CLICK") });
        var service = new BrowserInteractionService(page, gateway, new Compiler(PlanStepKind.Open, "Go to page 7 of the results"));
        var result = await service.RunAsync("go to page 7 of the results, then stop", scope: Scope);
        // Scrolling continues while the page is actually advancing (far past any small fixed count), by code alone, and is still bounded.
        var scrolls = page.Acts.Count(a => a.StartsWith("SCROLL"));
        Assert.InRange(scrolls, 7, TypeSafeBrowserDecisionSource.MaxScrollActions);
        Assert.True(gateway.Calls <= 1, $"model calls: {gateway.Calls}");
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public void TargetEvidence_IsGenericAndStructural()
    {
        var page7 = TargetEvidence.From("Go to page 7 of the search results");
        Assert.Equal(["7"], page7.Numbers);
        El(out var seven, "Page 7");
        El(out var next, "Next");
        Assert.True(TargetEvidence.Matches(page7, seven));
        Assert.False(TargetEvidence.Matches(page7, next));
        var third = TargetEvidence.From("the third result");
        Assert.True(TargetEvidence.Matches(third, seven with { Position = 3 }));
        Assert.False(TargetEvidence.Matches(third, seven with { Position = 1 }));
        // An ordinal on a page with no collection structure is left to the binder.
        Assert.True(TargetEvidence.AnyPlausible(third, [seven, next]));

        static void El(out EvidenceElement e, string name)
            => e = new("e", "link", name, null, null, false, true, true, null, null, null);
    }
}
