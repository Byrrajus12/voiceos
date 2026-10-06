using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// The Alpha correction invariants: revealing is not activating; a prerequisite that already holds costs no action; two real
/// options the binder cannot separate suspend the step and surface exactly those options; answering resumes the same step.
/// </summary>
public sealed class ChoiceAndRevealTests
{
    private static BrowserElement Link(string r, string name, string? href = null, string context = "")
        => new(r, "link", name, true, false, null, href, new(0, 0, 10, 10, true), context);

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

    private sealed class Gateway(Func<IReadOnlyDictionary<string, JevQuestionDto>, IReadOnlyDictionary<string, JevAnswer>> answer) : IJevGateway
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state, IReadOnlyDictionary<string, JevQuestionDto> q, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer(q));
        }
    }

    private static JevAnswer Dist(string selected, params (string Key, double P)[] ps)
        => new("choice", selected, ps.ToDictionary(p => p.Key, p => p.P), ps.Max(p => p.P));

    private sealed class Blocker(BlockerAssessment? result) : IBrowserBlockerAssessor
    {
        public int Calls { get; private set; }
        public ValueTask<BlockerAssessment?> AssessAsync(BlockerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>A scriptable page: a function of how many times each ref was clicked gives the current elements.</summary>
    private sealed class Site(Func<Site, IReadOnlyList<BrowserElement>> elements, Func<Site, IReadOnlyList<BrowserSection>?>? sections = null)
        : IChromeCompanionTransport
    {
        private int _n;
        public string Url { get; set; } = "https://site.example/start";
        public int ScrollY { get; set; }
        public bool SectionRevealed { get; private set; }
        /// <summary>A modal gate: the page behind it does not scroll.</summary>
        public bool ScrollLocked { get; set; }
        public List<string> Acts { get; } = [];
        public Action<Site, BrowserActionRequest>? OnAct { get; init; }
        public bool IsConnected => true;

        private BrowserSnapshot Snap(string session, int tab) => new(tab, session, $"r{++_n}", Url, "T", "text", false,
            new(1280, 800, 0, ScrollY, 6000), elements(this), Headings: sections?.Invoke(this));

        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => ValueTask.FromResult(Snap(s, 1));
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => ValueTask.FromResult(Snap(s, t));
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default)
        {
            Acts.Add($"{a.Action}:{a.ElementRef}");
            if (a.Action == "SCROLL" && a.ElementRef is not null) SectionRevealed = true;   // scrollIntoView
            else if (a.Action == "SCROLL" && !ScrollLocked) ScrollY += 600;
            OnAct?.Invoke(this, a);
            return ValueTask.FromResult(Snap(a.SessionId, a.TabId));
        }
    }

    private sealed class Recorder : Microsoft.Extensions.Logging.ILogger<BrowserInteractionService>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static readonly BrowserExecutionScope Active = new(BrowserScopeKind.ActiveTab, 1, "https://site.example/start", ExplicitSelection: true);

    // -- 1. reveal is not activate ----------------------------------------------------------------

    [Fact]
    public async Task ScrollToASection_RevealsIt_AndNeverActivatesALinkThatSharesItsName()
    {
        var site = new Site(_ => [Link("e1", "(4 Reviews)", "https://site.example/reviews")],
            s => [new("h1", "Customer Reviews", 2, s.SectionRevealed), new("h2", "Product details", 2, !s.SectionRevealed)]);
        // A model that would happily click the matching link, if it were ever asked.
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer>
            { ["operation"] = Dist("CLICK", ("CLICK", .95), ("BLOCKED", .05)), ["click_target"] = Dist("e1", ("e1", .95), ("e2", .05)) });
        var service = new BrowserInteractionService(site, gateway);

        var result = await service.RunAsync("Could you scroll down to the customer reviews section on this page?", scope: Active);

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["SCROLL:h1"], site.Acts);                      // the section was scrolled into view; nothing was clicked
        Assert.DoesNotContain(site.Acts, a => a.StartsWith("CLICK"));
        Assert.Equal(0, gateway.Calls);                               // and no model call was spent on it
    }

    [Theory]
    [InlineData("Could you scroll down to the customer reviews section on this page?", "customer reviews")]
    [InlineData("take me to the pricing section", "pricing")]
    [InlineData("find the shipping section on this page", "shipping")]
    [InlineData("go down to the FAQ", "FAQ")]
    [InlineData("show me the specifications on this page", "specifications")]
    public void RevealWording_IsRecognisedAsRevealing(string utterance, string section)
        => Assert.Equal(section, RevealIntent.Parse(utterance));

    [Theory]
    [InlineData("click the reviews link")]
    [InlineData("open the customer reviews section")]
    [InlineData("scroll down")]
    [InlineData("scroll to the bottom")]
    [InlineData("go to page 4")]
    [InlineData("take me to github.com")]
    [InlineData("scroll to the third result")]
    public void ActivationWordingOrAPagePosition_IsNotReveal(string utterance)
        => Assert.Null(RevealIntent.Parse(utterance));

    // -- 2. prerequisites that already hold -------------------------------------------------------

    [Fact]
    public async Task APrerequisiteThatAlreadyHolds_CompletesWithZeroActions_AndThePlanContinues()
    {
        var site = new Site(s => s.Url.EndsWith("/pricing") ? [] : [Link("e1", "Pricing", "https://site.example/pricing")])
        {
            OnAct = (s, a) => { if (a.Action == "CLICK") s.Url = "https://site.example/pricing"; }
        };
        var compiler = new Compiler(new(PlanStepKind.Act, "Dismiss the consent dialog"), new(PlanStepKind.Open, "Open Pricing", Target: "Pricing"));
        // The consent dialog is gone (an earlier session dealt with it): nothing to click, so the normal decision is stuck.
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)) });
        var blocker = new Blocker(new(BlockerKind.AlreadySatisfied, Evidence: "Pricing"));
        var service = new BrowserInteractionService(site, gateway, compiler, blocker: blocker);

        var result = await service.RunAsync("dismiss the consent dialog and open pricing", scope: Active);

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["CLICK:e1"], site.Acts);        // zero actions for the prerequisite; only the real target was opened
        Assert.Equal(1, blocker.Calls);
    }

    [Fact]
    public async Task AnAlreadySatisfiedClaim_IsNotAcceptedForTheLastStep_OrWithoutAssessorEvidence()
    {
        var site = new Site(_ => [Link("e1", "Home", "https://site.example/")]);
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)) });
        var compiler = new Compiler(new PlanStep(PlanStepKind.Act, "Dismiss the consent dialog"));
        var service = new BrowserInteractionService(site, gateway, compiler, blocker: new Blocker(new(BlockerKind.AlreadySatisfied, Evidence: "Home")));
        var result = await service.RunAsync("dismiss the consent dialog, then stop", scope: Active);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
    }

    // -- 3/4. a real choice, then resuming the same execution ---------------------------------------

    private static Site Songs()
    {
        var site = new Site(s => s.Url.EndsWith("/pricing") ? [] : s.Url.Contains("/track/")
            ? [Link("e1", "Pricing", "https://site.example/pricing")]
            : [Link("e1", "Hello", "https://site.example/track/adele", "Adele"), Link("e2", "Hello", "https://site.example/track/richie", "Lionel Richie")])
        {
            OnAct = (s, a) =>
            {
                if (a.Action != "CLICK") return;
                s.Url = s.Url.Contains("/track/") ? "https://site.example/pricing" : a.ElementRef == "e1" ? "https://site.example/track/adele" : "https://site.example/track/richie";
            }
        };
        return site;
    }

    private static Gateway Ambiguous() => new(_ => new Dictionary<string, JevAnswer>
    {
        ["operation"] = Dist("CLICK", ("CLICK", .9), ("BLOCKED", .1)),
        ["click_target"] = Dist("e1", ("e1", .5), ("e2", .5)),
        ["bind"] = Dist("e1", ("e1", .46), ("e2", .43), ("NONE", .06), ("e3", .05))
    });

    [Fact]
    public async Task TwoPlausibleGroundedOptions_SuspendTheStep_AndSurfaceTheirOwnWords()
    {
        var site = Songs();
        var gateway = Ambiguous();
        var service = new BrowserInteractionService(site, gateway, new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));

        var result = await service.RunAsync("open Hello, then stop", scope: Active);

        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        var pending = Assert.IsType<PendingChoice>(result.Pending);
        Assert.Equal(pending, service.PendingChoice);
        Assert.Equal("Which one?", pending.Reason);
        Assert.Equal(["Hello", "Hello"], pending.Options.Select(o => o.DisplayText));
        Assert.Equal(["Adele", "Lionel Richie"], pending.Options.Select(o => o.SecondaryText));   // the page's own nearby words
        Assert.Equal(["e1", "e2"], pending.Options.Select(o => o.TargetRef));
        Assert.Equal(2, pending.Options.Select(o => o.ChoiceId).Distinct().Count());
        Assert.Empty(site.Acts);                                                // nothing was guessed
        Assert.Equal(1, gateway.Calls);                                          // the options came from the binder's own answer
    }

    [Fact]
    public async Task ChoosingAnOption_ResumesTheSameStep_AndContinuesThePlan_WithoutRoutingOrCompiling()
    {
        var site = Songs();
        var gateway = Ambiguous();
        var compiler = new Compiler(new(PlanStepKind.Open, "Open Hello", Target: "Hello"), new(PlanStepKind.Open, "Open Pricing", Target: "Pricing"));
        var service = new BrowserInteractionService(site, gateway, compiler);
        var first = await service.RunAsync("open Hello then pricing", scope: Active);
        var pending = Assert.IsType<PendingChoice>(first.Pending);

        // A choice that is not one of this question's options is refused and the question stays open.
        Assert.Equal(BrowserStepMessages.ChoiceUnknown, (await service.ResumeChoiceAsync("not-an-option")).Detail);
        Assert.NotNull(service.PendingChoice);

        var second = pending.Options.Single(o => o.SecondaryText == "Lionel Richie");
        var resumed = await service.ResumeChoiceAsync(second.ChoiceId);

        Assert.Equal(InteractionCompletionState.Complete, resumed.Completion);
        Assert.Equal(["CLICK:e2", "CLICK:e1"], site.Acts);      // the exact option picked, then the rest of the plan
        Assert.Equal("https://site.example/pricing", resumed.Url);
        Assert.Equal(1, compiler.Calls);                          // nothing was re-compiled
        Assert.Equal(1, gateway.Calls);                           // nothing was re-decided
        Assert.Null(service.PendingChoice);
        Assert.Equal(BrowserStepMessages.ChoiceExpired, (await service.ResumeChoiceAsync(second.ChoiceId)).Detail);   // answered once
    }

    [Fact]
    public async Task AnOptionThatIsGoneFromThePage_IsNotGuessedAt()
    {
        var site = Songs();
        var service = new BrowserInteractionService(site, Ambiguous(), new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));
        var pending = Assert.IsType<PendingChoice>((await service.RunAsync("open Hello, then stop", scope: Active)).Pending);
        site.Url = "https://site.example/pricing";   // the page moved on while the question was open
        var resumed = await service.ResumeChoiceAsync(pending.Options[0].ChoiceId);
        Assert.Equal(InteractionCompletionState.Incomplete, resumed.Completion);
        Assert.Equal(BrowserStepMessages.ChoiceGone, resumed.Detail);
        Assert.Empty(site.Acts);
    }

    [Fact]
    public async Task ABlockerThatNeedsTheUsersPreference_SurfacesTheRealControls_AndResumesTheSameStep()
    {
        var site = new Site(s => s.Url.EndsWith("/home") ? [Link("e1", "Pricing", "https://site.example/home/pricing")]
            : [new("e1", "button", "I am over 18", true, false, null, null, new(0, 0, 10, 10, true), ""),
               new("e2", "button", "I am under 18", true, false, null, null, new(0, 0, 10, 10, true), "")])
        {
            ScrollLocked = true,
            OnAct = (s, a) =>
            {
                if (a.Action != "CLICK" || a.ElementRef != "e1") return;
                s.ScrollLocked = false;
                s.Url = s.Url.EndsWith("/home") ? "https://site.example/home/pricing" : "https://site.example/home";
            }
        };
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)),
            ["bind"] = Dist("NONE", ("NONE", .8), ("e1", .1), ("e2", .1)) });
        var blocker = new Blocker(new(BlockerKind.UserChoice, ["e1", "e2"], "The page needs a choice:"));
        var log = new Recorder();
        var service = new BrowserInteractionService(site, gateway, new Compiler(new PlanStep(PlanStepKind.Open, "Open Pricing", Target: "Pricing")), blocker: blocker, logger: log);

        var first = await service.RunAsync("open pricing, then stop", scope: Active);
        var pending = Assert.IsType<PendingChoice>(first.Pending);
        Assert.Equal("The page needs a choice:", pending.Reason);
        Assert.Equal(["I am over 18", "I am under 18"], pending.Options.Select(o => o.DisplayText));
        Assert.Equal(ChoiceResolution.ClearsBlocker, pending.Resolution);

        var resumed = await service.ResumeChoiceAsync(pending.Options[0].ChoiceId);

        // The chosen control was activated once, and the SAME step then found its own target with the page cleared.
        Assert.True(resumed.Completion == InteractionCompletionState.Complete, $"{resumed.Completion}: {resumed.Detail} acts={string.Join(',', site.Acts)} {string.Join(" | ", log.Lines)}");
        Assert.Equal(["SCROLL:", "CLICK:e1", "CLICK:e1"], site.Acts);   // one look further (the gate does not scroll), the chosen control, then the step's own target
        Assert.Equal(1, blocker.Calls);
    }

    [Fact]
    public async Task HumanOnlyChallenges_StopWithAnAccurateExplanation()
    {
        var site = new Site(_ => [Link("e1", "Verify", "https://site.example/verify")]);
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)) });
        var service = new BrowserInteractionService(site, gateway, new Compiler(new PlanStep(PlanStepKind.Act, "Continue to checkout")),
            blocker: new Blocker(new(BlockerKind.HumanRequired, HumanKind: "captcha")));
        var result = await service.RunAsync("continue to checkout, then stop", scope: Active);
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal(BrowserStepMessages.Captcha, result.Detail);
        Assert.Empty(site.Acts);
    }

    // -- 5. noise is not a choice -----------------------------------------------------------------

    [Fact]
    public async Task AWeakNoisyCandidateSet_NeverCreatesAChoiceList()
    {
        var links = Enumerable.Range(1, 40).Select(i => Link($"e{i}", $"Hello item {i}", $"https://site.example/i/{i}")).ToArray();
        var site = new Site(_ => links);
        var noisy = new Dictionary<string, double> { ["NONE"] = .12 };
        foreach (var l in links) noisy[l.Ref] = .88 / links.Length;      // forty candidates at ~2% each
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer>
        {
            ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)),
            ["bind"] = new("choice", "e1", noisy, .05)
        });
        var service = new BrowserInteractionService(site, gateway, new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));
        var result = await service.RunAsync("open Hello, then stop", scope: Active);
        Assert.Null(result.Pending);
        Assert.Null(service.PendingChoice);
        Assert.NotEqual(InteractionCompletionState.Uncertain, result.Completion);
    }

    [Fact]
    public async Task OneCandidateClearlyAhead_IsModerateUncertaintyNotAQuestion()
    {
        // The other control is a different thing entirely (it does not carry what the user said), so there is one item to pick.
        var site = new Site(s => s.Url.Contains("/track/") ? [Link("e1", "Pricing", "https://site.example/pricing")]
            : [Link("e1", "Hello", "https://site.example/track/adele", "Adele"), Link("e2", "Goodbye", "https://site.example/track/richie", "Lionel Richie")])
        { OnAct = (s, a) => { if (a.Action == "CLICK") s.Url = "https://site.example/track/adele"; } };
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer>
        {
            ["operation"] = Dist("CLICK", ("CLICK", .9), ("BLOCKED", .1)),
            ["click_target"] = Dist("e1", ("e1", .9), ("e2", .1)),
            ["bind"] = Dist("e1", ("e1", .86), ("e2", .10), ("NONE", .04))
        });
        var service = new BrowserInteractionService(site, gateway, new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));
        var result = await service.RunAsync("open Hello, then stop", scope: Active);
        Assert.Null(result.Pending);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["CLICK:e1"], site.Acts);
    }

    // -- numeric evidence ----------------------------------------------------------------------

    private static EvidenceElement El(string name, string? href = null, string? landmark = null, string? kind = null)
        => new("e", "link", name, href, null, false, true, true, kind, null, null, Landmark: landmark);

    [Fact]
    public void ADurationOrACount_IsNotAPageNumber()
    {
        var page4 = TargetEvidence.From("go to page 4");
        Assert.False(TargetEvidence.Matches(page4, El("4:08")));
        Assert.False(TargetEvidence.Matches(page4, El("4:08:15")));
        Assert.False(TargetEvidence.Matches(page4, El("4 reviews")));
        Assert.False(TargetEvidence.Matches(page4, El("(4 Reviews)")));
        Assert.False(TargetEvidence.Matches(page4, El("Top 4 gadgets of the year")));
        // Pagination: the page named, a bare number inside navigation, a bare number in a numbered series, a paging address.
        Assert.True(TargetEvidence.Matches(page4, El("Page 4")));
        Assert.True(TargetEvidence.Matches(page4, El("4", landmark: "navigation")));
        Assert.True(TargetEvidence.Matches(page4, El("4", "https://x.example/search?q=a&page=4")));
        var series = new[] { El("3"), El("4"), El("5") }.Select((e, i) => e with { Id = $"n{i}" }).ToArray();
        Assert.True(TargetEvidence.Matches(page4, series[1], series));
        // A lone bare number with no structure around it is not a page control.
        Assert.False(TargetEvidence.Matches(page4, El("4")));
        Assert.False(TargetEvidence.AnyPlausible(page4, [El("4:08"), El("4 reviews"), El("Next")]));
        Assert.True(TargetEvidence.AnyPlausible(page4, [El("4:08"), El("4", landmark: "navigation")]));
    }

    [Fact]
    public void ASpokenAnswer_CanBeMatchedAgainstThePendingOptions()
    {
        var now = DateTimeOffset.UtcNow;
        var choice = new PendingChoice("x", "s1", "Which one?", [
            new("a", "e1", "Hello", "Adele"), new("b", "e2", "Hello", "Lionel Richie")], now, now.AddMinutes(1));
        Assert.Equal("a", PendingChoiceMatcher.Match("the Adele one", choice)?.ChoiceId);
        Assert.Equal("b", PendingChoiceMatcher.Match("second", choice)?.ChoiceId);
        Assert.Null(PendingChoiceMatcher.Match("open the pricing page", choice));   // not an answer: a new command
        Assert.Null(PendingChoiceMatcher.Match("Hello", choice));                   // matches both: never guessed
    }

    // -- A1: AlreadySatisfied is about the step's role and positive evidence, not its kind ------------------

    [Fact]
    public async Task APrerequisiteRepresentedAsOpen_IsAlreadySatisfied_WhenItsStateIsPositivelyObserved()
    {
        var site = new Site(s => s.Url.EndsWith("/pricing") ? [] : [Link("e1", "Pricing", "https://site.example/pricing")])
        { OnAct = (s, a) => { if (a.Action == "CLICK") s.Url = "https://site.example/pricing"; } };
        var compiler = new Compiler(new(PlanStepKind.Open, "Open the age verification", Target: "age verification"), new(PlanStepKind.Open, "Open Pricing", Target: "Pricing"));
        var gateway = new Gateway(_ => new Dictionary<string, JevAnswer> { ["operation"] = Dist("BLOCKED", ("BLOCKED", .9), ("CLICK", .1)), ["bind"] = Dist("NONE", ("NONE", .9), ("e1", .1)) });
        var blocker = new Blocker(new(BlockerKind.AlreadySatisfied, Evidence: "Pricing"));
        var result = await new BrowserInteractionService(site, gateway, compiler, blocker: blocker).RunAsync("verify my age then open pricing", scope: Active);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["CLICK:e1"], site.Acts.Where(a => a.StartsWith("CLICK")));   // the prerequisite cost no action
    }

    [Fact]
    public void AlreadySatisfied_NeedsPositiveEvidence_AndNeverStandsInForTheFinalResult()
    {
        var obs = Observe("https://site.example/", "Welcome back", Link("e1", "Pricing"));
        var plan = new InteractionPlan("x", "x", [new(PlanStepKind.Open, "Open the age verification", Target: "age verification"), new(PlanStepKind.Open, "Open Pricing", Target: "Pricing")]);
        Assert.True(BlockerPolicy.MayBeAlreadySatisfied(plan, obs, "Welcome back"));
        Assert.False(BlockerPolicy.MayBeAlreadySatisfied(plan, obs, null));                  // absence alone proves nothing
        Assert.False(BlockerPolicy.MayBeAlreadySatisfied(plan, obs, "You may enter"));       // not on the page
        Assert.False(BlockerPolicy.MayBeAlreadySatisfied(plan, obs, "age verification"));    // only restates the step's own words
        Assert.False(BlockerPolicy.MayBeAlreadySatisfied(plan.Advance(), obs, "Welcome back"));   // the last step delivers the result
    }

    private static InteractionObservation Observe(string url, string text, params BrowserElement[] elements)
        => new(1, "k", System.Text.Json.JsonSerializer.Serialize(new { current_url = url, current_title = "t", visible_text = text,
            elements = elements.Select(e => new { id = e.Ref, Role = e.Role, Name = e.Name, Href = e.Href, Enabled = true }) }), []);

    // -- B: a Locate whose answer is the entity ------------------------------------------------------------

    private sealed class Extractor(string? value) : IBrowserValueExtractor
    {
        public int Calls { get; private set; }
        public ValueTask<string?> ExtractAsync(ValueRequest request, CancellationToken cancellationToken = default) { Calls++; return ValueTask.FromResult(value); }
    }

    [Fact]
    public async Task InformationalLocate_ResolvesTheEntity_AndFinishesWithoutClickingOrScrolling()
    {
        var site = new Site(_ => [Link("e1", "Supernatural", "https://tv.example/supernatural"), Link("e2", "The Boys", "https://tv.example/the-boys")]);
        var extractor = new Extractor("The Boys");
        var gateway = new Gateway(_ => throw new InvalidOperationException("no decision needed"));
        var compiler = new Compiler(new PlanStep(PlanStepKind.Locate, "Identify a different show featuring Jensen Ackles", Target: "a different show featuring Jensen Ackles"));
        var result = await new BrowserInteractionService(site, gateway, compiler, extractor: extractor)
            .RunAsync("find a different show Jensen Ackles is in, then stop", scope: Active);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal("The Boys", result.Resolved);
        Assert.Empty(site.Acts);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public void ADifferentShow_CannotBeTheOneTheRequestAlreadyNamed()
    {
        var step = new PlanStep(PlanStepKind.Locate, "Identify a different show featuring the actor", Target: "a different show");
        Assert.True(BrowserInteractionService.Excluded(step, "find the actor in Supernatural and a different show", "Supernatural"));
        Assert.False(BrowserInteractionService.Excluded(step, "find the actor in Supernatural and a different show", "The Boys"));
        Assert.False(BrowserInteractionService.Excluded(new PlanStep(PlanStepKind.Locate, "Find Supernatural", Target: "Supernatural"), "find Supernatural", "Supernatural"));
    }

    // -- C: compiler structure ---------------------------------------------------------------------------

    private static string PlanJson(params string[] steps) => "{\"finalGoal\":\"g\",\"endState\":\"ResourceOpened\",\"resourceType\":null,\"preferredService\":null,\"preferredServiceUrl\":null,\"steps\":[" +
        string.Join(",", steps) + "],\"correctedTerms\":[]}";
    private static string StepJson(string kind, string description, string? query = null, string? target = null, string? produces = null)
        => $"{{\"kind\":\"{kind}\",\"description\":\"{description}\",\"query\":{(query is null ? "null" : $"\"{query}\"")},\"target\":{(target is null ? "null" : $"\"{target}\"")},\"progress\":null,\"searchScope\":null,\"produces\":{(produces is null ? "null" : $"\"{produces}\"")}}}";

    [Fact]
    public void AUniqueMissingProducer_IsRepairedLocally_AndLogged()
    {
        var json = PlanJson(StepJson("Locate", "Identify the actor who played Sam", target: "actor who played Sam"),
            StepJson("Search", "Search shows with the actor", query: "${actor} recent shows"),
            StepJson("Locate", "Find a recent show featuring ${actor}", target: "recent show featuring ${actor}"),
            StepJson("Open", "Open ${show} on IMDb", target: "${show}"));
        var diagnostics = new OpenRouterBrowserStepCompiler.CompileDiagnostics();
        var task = OpenRouterBrowserStepCompiler.Parse(json, "x", diagnostics);
        Assert.NotNull(task);
        Assert.Equal(["actor", null, "show", null], task!.Plan.Steps.Select(s => s.Produces));
        Assert.Equal(["output=actor producer_step=1 consumer_step=2 reason=unique_dependency", "output=show producer_step=3 consumer_step=4 reason=unique_dependency"], diagnostics.Repairs);
    }

    [Fact]
    public void AnAmbiguousOrMisorderedProducer_IsRejected_WithTheExactReason()
    {
        var ambiguous = PlanJson(StepJson("Locate", "Find the first show", target: "first show"), StepJson("Locate", "Find the newest show", target: "newest show"),
            StepJson("Open", "Open ${show}", target: "${show}"));
        var d1 = new OpenRouterBrowserStepCompiler.CompileDiagnostics();
        Assert.Null(OpenRouterBrowserStepCompiler.Parse(ambiguous, "x", d1));
        Assert.Equal("unresolved_reference", d1.Reason);
        Assert.Equal("reference=show consumer_step=3 candidate_producers=2", d1.Detail);
        Assert.Contains("1:Locate[", d1.Plan);

        var after = PlanJson(StepJson("Open", "Open ${show}", target: "${show}"), StepJson("Locate", "Find the show", target: "show", produces: "show"));
        var d2 = new OpenRouterBrowserStepCompiler.CompileDiagnostics();
        Assert.Null(OpenRouterBrowserStepCompiler.Parse(after, "x", d2));
        Assert.Equal("producer_after_consumer", d2.Reason);
        Assert.Equal("reference=show consumer_step=1 producer_step=2", d2.Detail);
    }

    // -- D: infrastructure is not a semantic failure ---------------------------------------------------------

    private sealed class BrokenExtension : IChromeCompanionTransport
    {
        public bool IsConnected => true;
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string s, string u, CancellationToken c = default) => throw new ChromeCompanionException("EXTENSION_ERROR", "boom");
        public ValueTask<BrowserSnapshot> ObserveAsync(string s, int t, CancellationToken c = default) => throw new ChromeCompanionException("EXTENSION_ERROR", "boom");
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest a, CancellationToken c = default) => throw new ChromeCompanionException("EXTENSION_ERROR", "boom");
        public ValueTask SelectTabAsync(string s, int t, string u, bool r, CancellationToken c = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AnExtensionFailure_IsAnInfrastructureMessage_NotASemanticOne()
    {
        var service = new BrowserInteractionService(new BrokenExtension(), new Gateway(_ => new Dictionary<string, JevAnswer>()));
        var result = await service.RunAsync("scroll to the pricing section", scope: Active);
        Assert.Equal(UnavailableReason.ChromeCompanion, result.Unavailable);
        Assert.Equal("I lost the connection to Chrome.", result.Detail);
        Assert.Equal("I lost the connection to Chrome.", VoiceOS.Core.Activation.ActivityMessage.ForUnavailable(result.Unavailable));
    }

    // -- A4: the picked option is found again by its identity -----------------------------------------------

    [Fact]
    public async Task APickedOptionIsFoundAgainByItsIdentity_OrTheResumeFailsSafely()
    {
        var swapped = false;
        var site = new Site(_ => swapped
            ? [Link("e1", "Hello", "https://site.example/track/richie", "Lionel Richie"), Link("e2", "Hello", "https://site.example/track/adele", "Adele")]
            : [Link("e1", "Hello", "https://site.example/track/adele", "Adele"), Link("e2", "Hello", "https://site.example/track/richie", "Lionel Richie")])
        { OnAct = (s, a) => { if (a.Action == "CLICK") s.Url = "https://site.example/clicked"; } };
        var service = new BrowserInteractionService(site, Ambiguous(), new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));
        var pending = Assert.IsType<PendingChoice>((await service.RunAsync("open Hello, then stop", scope: Active)).Pending);
        swapped = true;   // the references now point at the other song
        await service.ResumeChoiceAsync(pending.Options.Single(o => o.SecondaryText == "Adele").ChoiceId);
        Assert.Equal(["CLICK:e2"], site.Acts);             // Adele moved to e2; the control at e1 (Richie) was never touched

        // Two same-named controls, neither of which leads where the option led: nothing is guessed.
        var moved = false;
        var twins = new Site(_ => moved
            ? [Link("e1", "Hello", "https://site.example/x", "Adele"), Link("e2", "Hello", "https://site.example/y", "Adele")]
            : [Link("e1", "Hello", "https://site.example/track/adele", "Adele"), Link("e2", "Hello", "https://site.example/track/richie", "Lionel Richie")]);
        var service2 = new BrowserInteractionService(twins, Ambiguous(), new Compiler(new PlanStep(PlanStepKind.Open, "Open Hello", Target: "Hello")));
        var pending2 = Assert.IsType<PendingChoice>((await service2.RunAsync("open Hello, then stop", scope: Active)).Pending);
        moved = true;
        var resumed = await service2.ResumeChoiceAsync(pending2.Options[0].ChoiceId);
        Assert.Equal(BrowserStepMessages.ChoiceGone, resumed.Detail);
        Assert.Empty(twins.Acts);
    }

    [Fact]
    public void LabelsThatSayNothing_AreNotAChoice()
    {
        var obs = Observe("https://site.example/", "", Link("e1", "Option", "https://site.example/a"), Link("e2", "Option", "https://site.example/a2"));
        Assert.Null(TypeSafeBrowserDecisionSource.GroundedOptions(obs, [("e1", .5), ("e2", .5)]));
    }
}
