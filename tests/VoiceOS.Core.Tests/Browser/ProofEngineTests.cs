using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ProofEngineTests
{
    private static readonly InteractionAction Click = new("r1:click:e1", InteractionActionKind.Activate, "e1");

    [Theory]
    [InlineData("https://a.test/x", "https://a.test/x", UrlRelation.Exact)]
    [InlineData("https://www.a.test/x/", "https://a.test/x#frag", UrlRelation.Exact)]
    [InlineData("HTTPS://A.TEST/x", "https://a.test/x", UrlRelation.Exact)]
    [InlineData("https://a.test/x", "https://a.test/y", UrlRelation.SameOrigin)]
    [InlineData("https://a.test/x", "https://docs.a.test/y", UrlRelation.SameOrigin)]
    [InlineData("https://docs.a.test/x", "https://a.test/y", UrlRelation.Inconsistent)]
    [InlineData("https://a.test/x", "https://a.test.evil.test/x", UrlRelation.Inconsistent)]
    [InlineData("https://a.test/x", "https://b.test/x", UrlRelation.Inconsistent)]
    [InlineData("https://a.test/x?p=1", "https://a.test/x?p=2", UrlRelation.SameOrigin)]
    [InlineData("https://a.test/url?q=https%3A%2F%2Fb.test%2Fpage&x=1", "https://b.test/page", UrlRelation.Embedded)]
    [InlineData("https://a.test/url?q=https%3A%2F%2Fb.test%2Fpage", "https://b.test/other", UrlRelation.Inconsistent)]
    [InlineData("https://user@a.test/x", "https://a.test/x", UrlRelation.Inconsistent)]
    [InlineData("javascript:void(0)", "https://a.test/x", UrlRelation.Inconsistent)]
    [InlineData(null, "https://a.test/x", UrlRelation.Inconsistent)]
    [InlineData("https://a.test/x", "about:blank", UrlRelation.Inconsistent)]
    public void UrlConsistency_Relates(string? href, string? landing, UrlRelation expected)
        => Assert.Equal(expected, UrlConsistency.Relate(href, landing));

    [Theory]
    [InlineData("https://a.test/", "https://b.test/", true)]
    [InlineData("https://a.test/", "https://docs.a.test/", false)]
    [InlineData("https://docs.a.test/", "https://a.test/", false)]
    [InlineData("https://x.a.test/", "https://y.a.test/", false)]
    [InlineData("https://a.localhost:1/", "https://b.localhost:1/", true)]
    [InlineData("not a url", "https://b.test/", false)]
    public void UrlConsistency_UnrelatedHosts(string a, string b, bool expected)
        => Assert.Equal(expected, UrlConsistency.UnrelatedHosts(a, b));

    // ── engine behavior ────────────────────────────────────────────────────────

    private static OutcomeStep ActivateStep() => new("s1", ProofFamily.Activate, new("the docs link"));

    private static async Task<(InteractionRunResult Result, Script Decisions, Script Surface)> Run(
        ProofMode mode, ProofVerdict? verdict, ProofFamily family = ProofFamily.Activate, bool acquired = false)
    {
        var surface = new Script(acquired);
        var decisions = new Script(acquired);
        var evaluator = new FakeEvaluator(verdict);
        var result = await new InteractionEngine(evaluator, mode).RunAsync(
            new("open docs", new OutcomeStep("s1", family, new("docs"))), surface, decisions, new InteractionBudget(6, 4, 3));
        return (result, decisions, surface);
    }

    [Fact]
    public async Task Off_NeverEvaluatesAndBehavesAsLegacy()
    {
        var (result, decisions, surface) = await Run(ProofMode.Off, ProofVerdict.Proved(ProofFamily.Activate, "r"));
        Assert.Null(result.Proof);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, decisions.Decides);
        Assert.Equal(1, surface.Assessments);
    }

    [Fact]
    public async Task Shadow_RecordsVerdictsButNeverChangesControlFlow()
    {
        var (result, decisions, surface) = await Run(ProofMode.Shadow, ProofVerdict.Proved(ProofFamily.Activate, "r"));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, decisions.Decides);            // the post-action decision still ran
        Assert.Equal(1, surface.Assessments);          // and so did the confirmation
        Assert.Contains(result.Proof!, r => r.Verdict.Status == ProofStatus.Proved && r.Actions == 1);
    }

    [Fact]
    public async Task Shadow_RefutedDoesNotStopTheRun()
    {
        var (result, decisions, _) = await Run(ProofMode.Shadow, ProofVerdict.Refuted(ProofFamily.Activate, "wrong_destination"));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, decisions.Decides);
    }

    [Fact]
    public async Task On_ProvedFinishesWithoutFurtherDecisionOrConfirmation()
    {
        var (result, decisions, surface) = await Run(ProofMode.On, ProofVerdict.Proved(ProofFamily.Activate, "link_followed_exact"));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal("proof:link_followed_exact", result.Detail);
        Assert.Equal(1, decisions.Decides);            // only the decision that chose the click
        Assert.Equal(0, surface.Assessments);
        Assert.Equal(1, result.Progress.Actions);
    }

    [Fact]
    public async Task On_OnlyActiveFamiliesMayEndTheRun_OthersStayShadow()
    {
        var surface = new Script(false);
        var decisions = new Script(false);
        var evaluator = new FakeEvaluator(ProofVerdict.Proved(ProofFamily.Find, "query_results_shown"));
        var result = await new InteractionEngine(evaluator, ProofMode.On, new HashSet<ProofFamily> { ProofFamily.Activate })
            .RunAsync(new("search", new OutcomeStep("s1", ProofFamily.Find, new("q"))), surface, decisions, new InteractionBudget(6, 4, 3));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, decisions.Decides);       // legacy ran to its own completion
        Assert.Equal(1, surface.Assessments);
        Assert.Contains(result.Proof!, r => r.Verdict.Family == ProofFamily.Find && r.Verdict.Status == ProofStatus.Proved);
    }

    [Fact]
    public async Task On_RefutedFinishesIncomplete_NotUncertain()
    {
        var (result, decisions, _) = await Run(ProofMode.On, ProofVerdict.Refuted(ProofFamily.Activate, "wrong_destination"));
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal("proof_refuted:wrong_destination", result.Detail);
        Assert.Equal(1, decisions.Decides);
    }

    [Theory]
    [InlineData(ProofStatus.NotYet)]
    [InlineData(ProofStatus.Inconclusive)]
    public async Task On_NotYetAndInconclusive_FallBackToTheLegacyPath(ProofStatus status)
    {
        var (result, decisions, surface) = await Run(ProofMode.On, new(ProofFamily.Activate, status, "r"));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(2, decisions.Decides);
        Assert.Equal(1, surface.Assessments);
    }

    [Fact]
    public async Task On_InitialSurfaceProofCompletesWithZeroDecisions()
    {
        var surface = new Script(acquired: true);
        var decisions = new Script(acquired: true);
        var evaluator = new FakeEvaluator(ProofVerdict.Proved(ProofFamily.Surface, "surface_acquired_on_destination"),
            initialOnly: true);
        var result = await new InteractionEngine(evaluator, ProofMode.On).RunAsync(
            new("open site", new OutcomeStep("s1", ProofFamily.Surface, new("site"))), surface, decisions, new InteractionBudget(6, 4, 3));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(0, decisions.Decides);
        Assert.Equal(0, result.Progress.Actions);
    }

    [Fact]
    public async Task On_WithoutAStep_IsExactlyLegacy()
    {
        var evaluator = new FakeEvaluator(ProofVerdict.Proved(ProofFamily.Activate, "r"));
        var decisions = new Script(false);
        var result = await new InteractionEngine(evaluator, ProofMode.On).RunAsync(
            new("open docs"), new Script(false), decisions, new InteractionBudget(6, 4, 3));
        Assert.Equal(0, evaluator.Calls);
        Assert.Equal(2, decisions.Decides);
        Assert.Null(result.Proof);
    }

    [Fact]
    public async Task On_BindingReachesTheEvaluatorAlongsideTheExecutedAction()
    {
        var evaluator = new FakeEvaluator(ProofVerdict.NotYet(ProofFamily.Activate, "r"));
        var binding = new TargetBinding("e1", 1, BindingMethod.JevChoice, 3, .9, .8);
        var decisions = new Script(false) { Binding = binding };
        await new InteractionEngine(evaluator, ProofMode.On).RunAsync(
            new("open docs", ActivateStep()), new Script(false), decisions, new InteractionBudget(6, 4, 3));
        Assert.Same(binding, evaluator.LastInput!.TargetBinding);
        Assert.Equal(Click.Id, evaluator.LastInput.LastAction!.Id);
        Assert.Contains(evaluator.LastInput.Effects, static e => e.Kind == EffectKind.Activated);
    }

    [Theory]
    [InlineData(ProofMode.On, InteractionCompletionState.Incomplete, "There is no earlier page in this tab.")]
    [InlineData(ProofMode.Shadow, InteractionCompletionState.Uncertain, null)]
    [InlineData(ProofMode.Off, InteractionCompletionState.Uncertain, null)]
    public async Task BackWithNoHistory_IsATypedFailureOnlyWhenProofIsOn(ProofMode mode, InteractionCompletionState expected, string? detail)
    {
        var back = new InteractionAction("r1:back", InteractionActionKind.GoBack);
        var surface = new BackSurface(back);
        var decisions = new BackDecisions(back);
        var result = await new InteractionEngine(null, mode).RunAsync(new("go back"), surface, decisions, new InteractionBudget(6, 4, 3));
        Assert.Equal(expected, result.Completion);
        if (detail is not null) Assert.Equal(detail, result.Detail);
        else Assert.NotEqual("There is no earlier page in this tab.", result.Detail);
        if (mode == ProofMode.On) Assert.Equal(1, decisions.Decides); // no BLOCKED/clarify round-trip
    }

    private sealed class BackDecisions(InteractionAction back) : IInteractionDecisionSource
    {
        public int Decides { get; private set; }
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
        {
            Decides++;
            return ValueTask.FromResult(Decides == 1 ? InteractionDecision.Act(back) : InteractionDecision.Unsure("blocked"));
        }
    }

    private sealed class BackSurface(InteractionAction back) : IInteractionSurface
    {
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken ct = default)
            => ValueTask.FromResult(new InteractionObservation(1, "same", "{}", [new("history", "History", [back])]));
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation, CancellationToken ct = default)
            => ValueTask.FromResult(InteractionActionResult.Fail(InteractionResultStatus.NoEffect, "no previous page",
                [new Effect(EffectKind.NoEffect, EffectSource.CompanionResponse, EffectStrength.Observed, null,
                    new Dictionary<string, string> { ["reason"] = "NO_HISTORY" })]));
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> history, CancellationToken ct = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Incomplete));
    }

    private sealed class FakeEvaluator(ProofVerdict? verdict, bool initialOnly = false) : IProofEvaluator
    {
        public int Calls { get; private set; }
        public ProofInput? LastInput { get; private set; }

        public ProofVerdict Evaluate(ProofInput input)
        {
            Calls++;
            LastInput = input;
            if (initialOnly && input.LastAction is not null) return ProofVerdict.NotYet(input.Step.Family, "initial_only");
            if (input.LastAction is null && !initialOnly) return ProofVerdict.NotYet(input.Step.Family, "no_action");
            return verdict ?? ProofVerdict.NotYet(input.Step.Family, "none");
        }
    }

    /// <summary>One click that changes the page, then DONE; doubles as surface and decision source.</summary>
    private sealed class Script(bool acquired) : IInteractionSurface, IInteractionDecisionSource
    {
        private int _observations;
        private bool _clicked;
        public int Decides { get; private set; }
        public int Assessments { get; private set; }
        public TargetBinding? Binding { get; init; }

        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken ct = default)
        {
            var effects = acquired && _observations++ == 0
                ? new[] { new Effect(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed) } : null;
            return ValueTask.FromResult(Observe(effects));
        }

        private InteractionObservation Observe(IReadOnlyList<Effect>? effects)
            => new(1, _clicked ? "after" : "before", "{}", [new("e1", "link 'Docs'", [Click])], effects);

        public ValueTask<InteractionObservation> ObserveAfterActionAsync(CancellationToken ct = default) => ObserveAsync(ct);

        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation, CancellationToken ct = default)
        {
            _clicked = true;
            return ValueTask.FromResult(InteractionActionResult.Ok("ok", [new Effect(EffectKind.Activated,
                EffectSource.CompanionResponse, EffectStrength.Observed)]));
        }

        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> history, CancellationToken ct = default)
        {
            Assessments++;
            return ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Complete, "done"));
        }

        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
        {
            Decides++;
            return ValueTask.FromResult(context.Progress.Actions == 0
                ? InteractionDecision.Act(Click) with { TargetBinding = Binding }
                : InteractionDecision.Done("done"));
        }
    }
}
