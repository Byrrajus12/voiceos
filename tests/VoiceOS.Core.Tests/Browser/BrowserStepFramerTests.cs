using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserStepFramerTests
{
    private static BrowserGoal Goal(SemanticEndState endState, string? resourceType = null, string? descriptor = null,
        string[]? queries = null, bool unresolved = false, string objective = "an objective")
        => BrowserGoal.FromUtterance("the utterance") with
        {
            Normalization = new(objective, null, resourceType, null, null, queries ?? [], "hint", [], endState,
                descriptor, unresolved)
        };

    [Theory]
    // Site-level SurfaceReady is Surface; a specific sub-resource stays model-judged (Reach).
    [InlineData(SemanticEndState.SurfaceReady, null, null, null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "  ", null, null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "website", null, null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "home page", "ignored", null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "jobs page", null, null, ProofFamily.Reach)]
    // ResultsVisible needs a grounded query.
    [InlineData(SemanticEndState.ResultsVisible, null, null, "dune", ProofFamily.Find)]
    [InlineData(SemanticEndState.ResultsVisible, null, null, null, ProofFamily.Reach)]
    [InlineData(SemanticEndState.ResultsVisible, null, null, " ", ProofFamily.Reach)]
    // ResourceOpened / ContentActive need a usable descriptor.
    [InlineData(SemanticEndState.ResourceOpened, null, "the third result", null, ProofFamily.Activate)]
    [InlineData(SemanticEndState.ResourceOpened, null, null, "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.ResourceOpened, null, "   ", null, ProofFamily.Reach)]
    [InlineData(SemanticEndState.ContentActive, null, "a video about tides", null, ProofFamily.Activate)]
    [InlineData(SemanticEndState.ContentActive, null, null, null, ProofFamily.Reach)]
    // Everything else, with or without a descriptor, is Reach.
    [InlineData(SemanticEndState.ResourceLocated, null, "the docs", "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.StateChanged, null, "page 7", "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.OtherBoundedGoal, null, "x", "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.Unspecified, null, "x", "q", ProofFamily.Reach)]
    public void Family_IsDerivedFromEndStateAndGroundedFields(SemanticEndState state, string? resource,
        string? descriptor, string? query, ProofFamily expected)
    {
        var goal = Goal(state, resource, descriptor, query is null ? null : [query]);
        Assert.Equal(expected, BrowserStepFramer.Frame(goal, "turn").Only.Family);
    }

    [Fact]
    public void EveryEndState_IsCoveredByTheTable()
    {
        // A new SemanticEndState must be considered deliberately: unknown states fall back to Reach.
        foreach (var state in Enum.GetValues<SemanticEndState>())
        {
            var family = BrowserStepFramer.Frame(Goal(state, descriptor: "d", queries: ["q"]), "t").Only.Family;
            Assert.True(Enum.IsDefined(family));
        }
    }

    [Fact]
    public void MissingNormalization_FramesAsReach()
    {
        var step = BrowserStepFramer.Frame(BrowserGoal.FromUtterance("open that thing"), "t").Only;
        Assert.Equal(ProofFamily.Reach, step.Family);
        Assert.Equal("open that thing", step.What.Phrase);
        Assert.True(step.DescriptorReliable);
    }

    [Fact]
    public void Plan_HasExactlyOneStep_WithTurnIdAndStableStepId()
    {
        var plan = BrowserStepFramer.Frame(Goal(SemanticEndState.ResultsVisible, queries: ["dune", "second"]), "turn-9");
        Assert.Equal("turn-9", plan.TurnId);
        Assert.Single(plan.Steps);
        Assert.Same(plan.Steps[0], plan.Only);
        Assert.Equal("s1", plan.Only.Id);
        Assert.Equal("dune", plan.Only.What.Query);
    }

    [Fact]
    public void Query_IsOnlyCarriedForFind()
    {
        var goal = Goal(SemanticEndState.ResourceOpened, descriptor: "the third result", queries: ["dune"]);
        var step = BrowserStepFramer.Frame(goal, "t").Only;
        Assert.Equal(ProofFamily.Activate, step.Family);
        Assert.Null(step.What.Query);
        Assert.Equal("the third result", step.What.Phrase);
    }

    [Fact]
    public void Phrase_PrefersDescriptorThenObjective()
    {
        Assert.Equal("the docs link", BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceLocated, descriptor: " the docs link "), "t").Only.What.Phrase);
        Assert.Equal("open a site", BrowserStepFramer.Frame(Goal(SemanticEndState.SurfaceReady, objective: "open a site"), "t").Only.What.Phrase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnresolvedReference_OnlyMarksTheDescriptorUnreliable_AndNeverChangesFamily(bool unresolved, bool reliable)
    {
        var goal = Goal(SemanticEndState.ResourceOpened, descriptor: "its pilot", unresolved: unresolved);
        var step = BrowserStepFramer.Frame(goal, "t").Only;
        Assert.Equal(ProofFamily.Activate, step.Family);
        Assert.Equal(reliable, step.DescriptorReliable);
    }

    [Fact]
    public void CommandPlan_RejectsAnythingButOneStep()
    {
        var step = new OutcomeStep("s1", ProofFamily.Reach, new("x"));
        Assert.Throws<ArgumentException>(() => new CommandPlan("t", []));
        Assert.Throws<ArgumentException>(() => new CommandPlan("t", [step, step with { Id = "s2" }]));
    }

    [Fact]
    public async Task Engine_LedgerUsesTheStepId_AndAStepDoesNotChangeControlFlow()
    {
        async Task<InteractionRunResult> Run(OutcomeStep? step) => await new InteractionEngine().RunAsync(
            new("go", step), new DoneSurface(), new DoneDecisions(), new InteractionBudget(3, 3, 3));

        var plain = await Run(null);
        var framed = await Run(new OutcomeStep("s9", ProofFamily.Activate, new("the link")));
        Assert.Equal(plain.Completion, framed.Completion);
        Assert.Equal(plain.Progress, framed.Progress);
        Assert.Equal(plain.Detail, framed.Detail);
        Assert.All(framed.Effects!, effect => Assert.Equal("s9", effect.StepId));
        Assert.Equal("s9.1", framed.Effects![0].Id);
        Assert.Equal("s1", plain.Effects![0].StepId);
    }

    private sealed class DoneDecisions : IInteractionDecisionSource
    {
        public ValueTask<InteractionDecision> DecideAsync(InteractionDecisionContext context, CancellationToken ct = default)
            => ValueTask.FromResult(InteractionDecision.Done("done"));
    }

    private sealed class DoneSurface : IInteractionSurface
    {
        private bool _first = true;
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken ct = default)
        {
            var effects = _first ? new[] { new Effect(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed) } : null;
            _first = false;
            return ValueTask.FromResult(new InteractionObservation(1, "k", "{}", [], effects));
        }
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action, InteractionObservation observation, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal, InteractionObservation observation,
            IReadOnlyList<InteractionHistoryEntry> history, CancellationToken ct = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Complete, "done"));
    }
}
