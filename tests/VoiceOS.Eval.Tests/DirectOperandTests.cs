using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>Typed media/volume operand expectations against the attempted direct steps.</summary>
public class DirectOperandTests
{
    private static TurnRecord Direct(params AttemptedDirectStep[] steps) => TestHelpers.Turn(
        OutcomeClass.Complete, CommandRoute.DirectCapability, ExecutionScopeKind.DirectCapability,
        programSteps: steps.Select(static s => s.Kind).ToArray(), attempted: steps);

    private static AttemptedDirectStep Media(MediaOperation op) => new("s1", "MediaControl", MediaOperation: op);

    [Fact]
    public void MatchingMediaOperationPasses()
    {
        var turn = ScenarioEvaluator.EvaluateTurn(Direct(Media(MediaOperation.Previous)),
            new Expectation(Outcome: ExpectedOutcome.Complete, MediaOperation: MediaOperation.Previous));
        Assert.Equal(Classification.Pass, turn.Classification);
    }

    [Fact]
    public void WrongMediaOperationIsWrongAction()
    {
        var turn = ScenarioEvaluator.EvaluateTurn(Direct(Media(MediaOperation.Next)),
            new Expectation(Outcome: ExpectedOutcome.Complete, MediaOperation: MediaOperation.Previous));
        Assert.Equal(Classification.WrongAction, turn.Classification);
        var check = Assert.Single(turn.Checks, static c => c.Name == "mediaOperation");
        Assert.Equal("Next", check.Actual);
    }

    [Fact]
    public void MissingMediaStepFailsTheOperationCheck()
    {
        var turn = ScenarioEvaluator.EvaluateTurn(Direct(new AttemptedDirectStep("s1", "FocusWindow")),
            new Expectation(Outcome: ExpectedOutcome.Complete, MediaOperation: MediaOperation.Pause));
        Assert.Equal(Classification.WrongAction, turn.Classification);
    }

    [Fact]
    public void WithoutOperandExpectationsBehaviorIsUnchanged()
    {
        var turn = ScenarioEvaluator.EvaluateTurn(Direct(Media(MediaOperation.Next)),
            new Expectation(Outcome: ExpectedOutcome.Complete,
                DirectSteps: new StringSetExpectation(Include: ["MediaControl"])));
        Assert.Equal(Classification.Pass, turn.Classification);
        Assert.DoesNotContain(turn.Checks, static c => c.Name is "mediaOperation" or "setVolume" or "adjustVolume");
    }

    [Theory]
    [InlineData(35, Classification.Pass)]
    [InlineData(50, Classification.WrongAction)]
    public void SetVolumeComparesTheExecutedLevel(int executed, Classification expected)
    {
        var turn = ScenarioEvaluator.EvaluateTurn(Direct(new AttemptedDirectStep("s1", "SetVolume", VolumeValue: executed)),
            new Expectation(Outcome: ExpectedOutcome.Complete, SetVolume: 35));
        Assert.Equal(expected, turn.Classification);
    }

    [Theory]
    [InlineData(VolumeDirection.Down, null, null, Classification.Pass)]
    [InlineData(VolumeDirection.Up, null, null, Classification.WrongAction)]
    [InlineData(VolumeDirection.Down, 10, 10, Classification.Pass)]
    [InlineData(VolumeDirection.Down, 10, 20, Classification.WrongAction)]
    public void AdjustVolumeComparesDirectionAndOptionalAmount(VolumeDirection executed, int? expectedAmount,
        int? executedAmount, Classification expected)
    {
        var turn = ScenarioEvaluator.EvaluateTurn(
            Direct(new AttemptedDirectStep("s1", "AdjustVolume", VolumeDirection: executed, VolumeAmount: executedAmount)),
            new Expectation(Outcome: ExpectedOutcome.Complete,
                AdjustVolume: new AdjustVolumeExpectation(VolumeDirection.Down, expectedAmount)));
        Assert.Equal(expected, turn.Classification);
    }
}
