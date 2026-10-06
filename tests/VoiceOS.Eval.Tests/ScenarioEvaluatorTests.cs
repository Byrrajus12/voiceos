using VoiceOS.Core.Browser;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public class ScenarioEvaluatorTests
{
    [Fact]
    public void PassWhenOutcomeRouteAndScopeMatch()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, CommandRoute.DirectCapability, ExecutionScopeKind.DirectCapability);
        var expect = new Expectation(ExpectedOutcome.Complete, [CommandRoute.DirectCapability], [ScopeExpectation.Direct]);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.Pass, result.Classification);
    }

    [Fact]
    public void CorrectClarifyWhenExpectedAndActualBothClarify()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Clarify);
        var expect = new Expectation(ExpectedOutcome.Clarify);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.CorrectClarify, result.Classification);
        Assert.Contains(result.Classification, ScenarioResult.SuccessSet);
    }

    [Fact]
    public void UnnecessaryClarifyWhenCompleteExpectedButClarified()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Clarify);
        var expect = new Expectation(ExpectedOutcome.Complete);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.UnnecessaryClarify, result.Classification);
    }

    [Fact]
    public void MissedClarifyWhenClarifyExpectedButCompleted()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete);
        var expect = new Expectation(ExpectedOutcome.Clarify);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.MissedClarify, result.Classification);
    }

    [Fact]
    public void WrongRouteWhenRouteDoesNotMatchExpectation()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, CommandRoute.ComputerUse, ExecutionScopeKind.Browser,
            BrowserScopeKind.NewTaskTab);
        var expect = new Expectation(ExpectedOutcome.Complete, [CommandRoute.DirectCapability]);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.WrongRoute, result.Classification);
    }

    [Fact]
    public void WrongScopeWhenScopeDoesNotMatchExpectation()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, CommandRoute.ComputerUse, ExecutionScopeKind.Browser,
            BrowserScopeKind.NewTaskTab);
        var expect = new Expectation(ExpectedOutcome.Complete, Scope: [ScopeExpectation.Direct]);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.WrongScope, result.Classification);
    }

    [Fact]
    public void WrongActionWhenNewTabsExceedsMax()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, tabsCreated: 2);
        var expect = new Expectation(ExpectedOutcome.Complete, NewTabs: new NewTabsExpectation(Max: 1));
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.WrongAction, result.Classification);
    }

    [Fact]
    public void FalseSuccessWhenClaimedCompleteButNothingChanged()
    {
        var unchanged = TestHelpers.Snapshot(fgProcess: "chrome", fgTitle: "Chrome");
        var turn = TestHelpers.Turn(OutcomeClass.Complete, initial: unchanged, final: unchanged);
        var expect = new Expectation(ExpectedOutcome.Complete, Final: new FinalExpectation(ForegroundProcess: "notepad"));
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.FalseSuccess, result.Classification);
    }

    [Fact]
    public void WrongTargetWhenStateChangedButNotToExpectedTarget()
    {
        var initial = TestHelpers.Snapshot(fgProcess: "chrome", fgTitle: "Chrome");
        var final = TestHelpers.Snapshot(fgProcess: "explorer", fgTitle: "File Explorer");
        var turn = TestHelpers.Turn(OutcomeClass.Complete, initial: initial, final: final);
        var expect = new Expectation(ExpectedOutcome.Complete, Final: new FinalExpectation(ForegroundProcess: "notepad"));
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.WrongTarget, result.Classification);
    }

    [Fact]
    public void PartialWhenOnlyEfficiencyCheckFails()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, actions: 10);
        var expect = new Expectation(ExpectedOutcome.Complete, MaxActions: 2);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.Partial, result.Classification);
    }

    [Fact]
    public void TimeoutClassifiesAsTimeoutRegardlessOfExpectation()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Timeout);
        var expect = new Expectation(ExpectedOutcome.Complete);
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.Timeout, result.Classification);
    }

    [Fact]
    public void ExecutionFailureWhenFailurePresent()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Failed, failure: "boom");
        var result = ScenarioEvaluator.EvaluateTurn(turn, new Expectation(ExpectedOutcome.Complete));
        Assert.Equal(Classification.ExecutionFailure, result.Classification);
    }

    [Fact]
    public void EvaluateScenarioUsesFirstNonSuccessTurn()
    {
        var pass = TestHelpers.Turn(OutcomeClass.Complete) with { Classification = Classification.Pass };
        var wrongRoute = TestHelpers.Turn(OutcomeClass.Complete) with { Classification = Classification.WrongRoute };
        var laterPass = TestHelpers.Turn(OutcomeClass.Complete) with { Classification = Classification.Pass };
        Assert.Equal(Classification.WrongRoute, ScenarioEvaluator.EvaluateScenario([pass, wrongRoute, laterPass]));
    }

    [Fact]
    public void EvaluateScenarioUsesLastTurnWhenAllSucceed()
    {
        var pass = TestHelpers.Turn(OutcomeClass.Complete) with { Classification = Classification.Pass };
        var clarify = TestHelpers.Turn(OutcomeClass.Clarify) with { Classification = Classification.CorrectClarify };
        Assert.Equal(Classification.CorrectClarify, ScenarioEvaluator.EvaluateScenario([pass, clarify]));
    }
}
