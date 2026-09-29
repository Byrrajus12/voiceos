using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserStepFramerTests
{
    private static BrowserGoal Goal(SemanticEndState endState, string? resourceType = null,
        string[]? queries = null, string objective = "an objective", string? entity = null)
        => BrowserGoal.FromUtterance("the utterance") with
        {
            Normalization = new(objective, entity, resourceType, null, null, queries ?? [], "hint", [], endState)
        };

    [Theory]
    // Site-level SurfaceReady is Surface; a specific sub-resource stays model-judged (Reach).
    [InlineData(SemanticEndState.SurfaceReady, null, null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "  ", null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "website", null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "home page", null, ProofFamily.Surface)]
    [InlineData(SemanticEndState.SurfaceReady, "jobs page", null, ProofFamily.Reach)]
    // ResultsVisible needs a grounded query.
    [InlineData(SemanticEndState.ResultsVisible, null, "dune", ProofFamily.Find)]
    [InlineData(SemanticEndState.ResultsVisible, null, null, ProofFamily.Reach)]
    [InlineData(SemanticEndState.ResultsVisible, null, " ", ProofFamily.Reach)]
    // Opening a resource or activating content is Activate; the objective is the descriptor.
    [InlineData(SemanticEndState.ResourceOpened, null, "q", ProofFamily.Activate)]
    [InlineData(SemanticEndState.ContentActive, null, null, ProofFamily.Activate)]
    // Everything else is Reach.
    [InlineData(SemanticEndState.ResourceLocated, null, "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.StateChanged, null, "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.OtherBoundedGoal, null, "q", ProofFamily.Reach)]
    [InlineData(SemanticEndState.Unspecified, null, "q", ProofFamily.Reach)]
    public void Family_IsDerivedFromEndStateAndGroundedFields(SemanticEndState state, string? resource,
        string? query, ProofFamily expected)
    {
        var goal = Goal(state, resource, query is null ? null : [query]);
        Assert.Equal(expected, BrowserStepFramer.Frame(goal, "turn").Only.Family);
    }

    [Fact]
    public void EveryEndState_IsCoveredByTheTable()
    {
        // A new SemanticEndState must be considered deliberately: unknown states fall back to Reach.
        foreach (var state in Enum.GetValues<SemanticEndState>())
        {
            var family = BrowserStepFramer.Frame(Goal(state, queries: ["q"]), "t").Only.Family;
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
        var goal = Goal(SemanticEndState.ResourceOpened, queries: ["dune"], objective: "open the third result");
        var step = BrowserStepFramer.Frame(goal, "t").Only;
        Assert.Equal(ProofFamily.Activate, step.Family);
        Assert.Null(step.What.Query);
        Assert.Equal("open the third result", step.What.Phrase);
    }

    [Fact]
    public void Descriptor_IsTheObjective_WithTheUtteranceKeptAsContext()
    {
        var step = BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceLocated, objective: "the docs link"), "t").Only;
        Assert.Equal("the docs link", step.What.Phrase);
        Assert.Equal("the utterance", step.What.Utterance);
    }

    private static BrowserExecutionScope Scope(ContextDependency dependency, TaskRelation relation, bool established = true)
        => new(BrowserScopeKind.ActiveTab) { ContextDependency = dependency, TaskRelation = relation, TaskRelationEstablished = established };

    [Theory]
    [InlineData(ContextDependency.SelfContained, TaskRelation.NewTask, true, true)]
    [InlineData(ContextDependency.RequiresCurrentSurface, TaskRelation.NewTask, true, true)]
    [InlineData(ContextDependency.Uncertain, TaskRelation.NewTask, true, false)]
    [InlineData(ContextDependency.SelfContained, TaskRelation.ContinueRecent, true, false)]
    [InlineData(ContextDependency.RequiresCurrentSurface, TaskRelation.RequiresRecent, true, false)]
    [InlineData(ContextDependency.SelfContained, TaskRelation.NewTask, false, false)]
    public void ActivateAndFindReliability_RequiresAnEstablishedFreshTaskWithAResolvableReferent(
        ContextDependency dependency, TaskRelation relation, bool established, bool reliable)
    {
        var scope = Scope(dependency, relation, established);
        Assert.Equal(reliable, BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t", scope).Only.DescriptorReliable);
        Assert.Equal(reliable, BrowserStepFramer.Frame(Goal(SemanticEndState.ResultsVisible, queries: ["q"]), "t", scope).Only.DescriptorReliable);
    }

    [Fact]
    public void WithoutRouterSignals_ActivateAndFindAreUnreliable_ButSurfaceAndReachAreNot()
    {
        Assert.False(BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t").Only.DescriptorReliable);
        Assert.True(BrowserStepFramer.Frame(Goal(SemanticEndState.SurfaceReady), "t").Only.DescriptorReliable);
        Assert.True(BrowserStepFramer.Frame(Goal(SemanticEndState.OtherBoundedGoal), "t").Only.DescriptorReliable);
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
