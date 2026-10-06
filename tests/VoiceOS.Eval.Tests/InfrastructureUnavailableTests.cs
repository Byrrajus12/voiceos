using VoiceOS.Core.Activation;
using VoiceOS.Core.Interaction;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public sealed class InfrastructureUnavailableTests
{
    [Fact]
    public void RunMapper_UnavailablePhase_MapsToUnavailableClass()
    {
        var run = new ActivationRun("test", InteractionKind.Command, ActivationSource.InjectedTranscript);
        typeof(ActivationRun).GetMethod("AddSnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(run, [new ApplicationInteractionSnapshot(ApplicationInteractionPhase.Unavailable, Unavailable: UnavailableReason.IntentService)]);
        var record = RunMapper.Map(0, "test", run, TestHelpers.Snapshot(), TestHelpers.Snapshot());
        Assert.Equal(OutcomeClass.Unavailable, record.OutcomeClass);
    }

    [Theory]
    [InlineData(ExpectedOutcome.Complete)] [InlineData(ExpectedOutcome.Clarify)]
    [InlineData(ExpectedOutcome.Failed)] [InlineData(ExpectedOutcome.Unsupported)]
    public void ScenarioEvaluator_Unavailable_IsInfrastructureUnavailable_NeverUnnecessaryClarify(ExpectedOutcome expected)
    {
        var result = ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Unavailable), new Expectation { Outcome = expected });
        Assert.Equal(Classification.InfrastructureUnavailable, result.Classification);
        Assert.DoesNotContain(result.Classification, ScenarioResult.SuccessSet);
    }

    [Fact]
    public void Aggregator_CountsInfrastructureUnavailableSeparately()
    {
        var r = new ScenarioResult("run", "id", "test", "test", [], 1, DateTimeOffset.UtcNow, 1,
            Classification.InfrastructureUnavailable, false, [], [], [], [], null);
        var summary = EvalAggregator.Summarize([r, r, r, r]);
        Assert.Equal(4, summary.InfrastructureUnavailable);
        Assert.False(summary.ComparisonValid);
        Assert.Equal(0, summary.UnnecessaryClarifyRate);
    }
}
