using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Interaction;

public sealed class InteractionEngineTests
{
    private static readonly InteractionAction Action = new("activate-save", InteractionActionKind.Activate, "save");

    [Theory]
    [InlineData(InteractionResultStatus.StaleTarget)]
    [InlineData(InteractionResultStatus.OccludedTarget)]
    public async Task SurfaceFailures_AreStructuredHistory(InteractionResultStatus status)
    {
        var surface = new FakeSurface(Observation("same"), [InteractionActionResult.Fail(status, "blocked")]);
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Act(Action)), noProgress: 1);

        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal(status, Assert.Single(result.RecentHistory).Result.Status);
        Assert.False(result.RecentHistory[0].Suppressed);
    }

    [Fact]
    public async Task RepeatedIdenticalFailure_OnUnchangedState_IsSuppressed()
    {
        var surface = new FakeSurface(Observation("same"), [
            InteractionActionResult.Fail(InteractionResultStatus.StaleTarget, "stale")
        ]);
        var result = await Run(surface, new FakeDecisions(
            InteractionDecision.Act(Action), InteractionDecision.Act(Action)), noProgress: 3);

        Assert.Equal(1, surface.ExecutionCount);
        Assert.Equal(2, result.RecentHistory.Count);
        Assert.True(result.RecentHistory[1].Suppressed);
    }

    [Fact]
    public async Task ReportedSuccessWithoutStateChange_BecomesNoEffect()
    {
        var surface = new FakeSurface(Observation("same"), [InteractionActionResult.Ok()]);
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Act(Action)), noProgress: 1);

        Assert.Equal(InteractionResultStatus.NoEffect, Assert.Single(result.RecentHistory).Result.Status);
        Assert.Equal(0, result.Progress.StateChanges);
    }

    [Fact]
    public async Task RejectedDone_IsNotProposedRepeatedlyAgainstSameEvidence()
    {
        var surface = new FakeSurface(Observation("same"), [],
            new(InteractionCompletionState.Incomplete, "not done"));
        var result = await Run(surface, new FakeDecisions(
            InteractionDecision.Done(), InteractionDecision.Done()), noProgress: 3);

        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal(1, surface.CompletionAssessmentCount);
        Assert.True(result.RecentHistory[^1].Suppressed);
    }

    [Fact]
    public async Task UncertainDecision_StopsWithoutExecution()
    {
        var surface = new FakeSurface(Observation("same"), []);
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Unsure("ambiguous")));

        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal("ambiguous", result.Detail);
        Assert.Equal(0, surface.ExecutionCount);
    }

    [Fact]
    public async Task UnadvertisedAction_FailsClosedAsScopeViolation()
    {
        var other = new InteractionAction("other", InteractionActionKind.Activate, "other");
        var surface = new FakeSurface(Observation("same"), []);
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Act(other)), noProgress: 1);

        Assert.Equal(InteractionResultStatus.ScopeViolation, Assert.Single(result.RecentHistory).Result.Status);
        Assert.Equal(0, surface.ExecutionCount);
    }

    private static ValueTask<InteractionRunResult> Run(
        FakeSurface surface,
        FakeDecisions decisions,
        int noProgress = 2)
        => new InteractionEngine().RunAsync(
            new("save the document"), surface, decisions,
            new InteractionBudget(MaxDecisions: 5, MaxActions: 5, MaxConsecutiveNoProgress: noProgress));

    private static InteractionObservation Observation(string key)
        => new(1, key, "Save button visible", [new("save", "Save", [Action])]);

    private sealed class FakeDecisions(params InteractionDecision[] decisions) : IInteractionDecisionSource
    {
        private readonly Queue<InteractionDecision> _decisions = new(decisions);

        public ValueTask<InteractionDecision> DecideAsync(
            InteractionDecisionContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_decisions.Dequeue());
    }

    private sealed class FakeSurface(
        InteractionObservation observation,
        IEnumerable<InteractionActionResult> results,
        InteractionCompletionAssessment? completion = null) : IInteractionSurface
    {
        private readonly Queue<InteractionActionResult> _results = new(results);
        public int ExecutionCount { get; private set; }
        public int CompletionAssessmentCount { get; private set; }

        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(observation);

        public ValueTask<InteractionActionResult> ExecuteAsync(
            InteractionAction action,
            InteractionObservation current,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(_results.Dequeue());
        }

        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(
            InteractionGoal goal,
            InteractionObservation current,
            CancellationToken cancellationToken = default)
        {
            CompletionAssessmentCount++;
            return ValueTask.FromResult(completion ?? new(InteractionCompletionState.Complete));
        }
    }
}
