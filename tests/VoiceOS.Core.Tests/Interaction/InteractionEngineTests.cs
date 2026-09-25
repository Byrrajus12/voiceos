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
    public async Task CompletionAccepted_FinishesAfterFreshAssessment()
    {
        var surface = new FakeSurface(Observation("complete"), [],
            new(InteractionCompletionState.Complete, "visible result"));
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Done()));

        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal("visible result", result.Detail);
        Assert.Equal(1, surface.CompletionAssessmentCount);
    }

    [Fact]
    public async Task CompletionUncertainty_ReturnsApplicationChoices()
    {
        var surface = new FakeSurface(Observation("uncertain"), [],
            new(InteractionCompletionState.Uncertain, "could be done"));
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Done()));

        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal(["complete", "continue", "cancel"], result.Choices!.Select(static choice => choice.Id));
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

    [Fact]
    public async Task RepeatedChangingPageWithoutGoalProgressStops()
    {
        var surface = new ChurningSurface();
        var decisions = new FakeDecisions(
            InteractionDecision.Act(Action) with { GoalConfidence = .1 },
            InteractionDecision.Act(Action) with { GoalConfidence = .1 },
            InteractionDecision.Act(Action) with { GoalConfidence = .1 },
            InteractionDecision.Act(Action) with { GoalConfidence = .1 });
        var result = await new InteractionEngine().RunAsync(new("change a setting"), surface,
            decisions, new InteractionBudget(10, 10, 2));
        Assert.Equal(InteractionCompletionState.Incomplete, result.Completion);
        Assert.Equal(3, surface.ExecutionCount);
        Assert.True(result.Progress.StateChanges >= 3);
    }

    [Fact]
    public async Task AmbiguousTabTopologyStopsBeforeRepeatingSourceAction()
    {
        var surface = new FakeSurface(Observation("source"), [
            InteractionActionResult.Fail(InteractionResultStatus.TopologyAmbiguous,
                "Several tabs appeared")]);
        var result = await Run(surface, new FakeDecisions(InteractionDecision.Act(Action)));
        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal(1, surface.ExecutionCount);
        Assert.Equal(1, result.Progress.Actions);
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
            IReadOnlyList<InteractionHistoryEntry> recentHistory,
            CancellationToken cancellationToken = default)
        {
            CompletionAssessmentCount++;
            return ValueTask.FromResult(completion ?? new(InteractionCompletionState.Complete));
        }
    }

    private sealed class ChurningSurface : IInteractionSurface
    {
        private int _observation;
        public int ExecutionCount { get; private set; }
        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
        {
            var revision = ++_observation;
            var evidence = $"{{\"current_url\":\"https://example.org/\",\"elements\":[{{\"version\":{revision}}}]}}";
            return ValueTask.FromResult(new InteractionObservation(revision, $"s{revision}", evidence,
                [new("save", "Save", [Action])]));
        }
        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action,
            InteractionObservation observation, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(InteractionActionResult.Ok());
        }
        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal,
            InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> recentHistory,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new InteractionCompletionAssessment(InteractionCompletionState.Incomplete));
    }
}
