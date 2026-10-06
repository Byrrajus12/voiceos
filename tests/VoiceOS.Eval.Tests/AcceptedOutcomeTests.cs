using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>Multiple acceptable outcomes and correctness/efficiency separation.</summary>
public sealed class AcceptedOutcomeTests
{
    private static readonly Expectation OpenOrClarify = new(ExpectedOutcome.Complete,
        NewTabs: new NewTabsExpectation(Max: 1),
        Final: new FinalExpectation(ActiveTabOriginContains: "imdb.com"),
        AcceptedOutcomes: [ExpectedOutcome.Complete, ExpectedOutcome.Clarify]);

    [Fact]
    public void PreferredOutcomeKeepsItsPositiveChecks()
    {
        var reached = TestHelpers.Turn(OutcomeClass.Complete, tabsCreated: 1,
            final: TestHelpers.Snapshot(activeUrl: "https://www.imdb.com/"));
        var wrong = TestHelpers.Turn(OutcomeClass.Complete, tabsCreated: 1,
            final: TestHelpers.Snapshot(activeUrl: "https://www.google.com/search?q=imdb"));
        var passed = ScenarioEvaluator.EvaluateTurn(reached, OpenOrClarify);
        Assert.Equal(Classification.Pass, passed.Classification);
        Assert.Empty(passed.EfficiencyMisses);
        Assert.NotEqual(Classification.Pass, ScenarioEvaluator.EvaluateTurn(wrong, OpenOrClarify).Classification);
    }

    [Fact]
    public void AlternateOutcomePassesOnGuardsAndRecordsPreferenceMiss()
    {
        var result = ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Clarify), OpenOrClarify);
        Assert.Equal(Classification.CorrectClarify, result.Classification);
        Assert.Contains(result.Checks, c => c.Name == "outcome" && c.Passed && c.Expected == "Complete|Clarify");
        Assert.Equal(["outcome.preference"], result.EfficiencyMisses);
    }

    [Fact]
    public void AlternateOutcomeStillFailsItsGuards()
    {
        var expect = OpenOrClarify with { NoMediaCommand = true };
        var strayTabs = ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Clarify, tabsCreated: 2), expect);
        var media = ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Clarify, programSteps: ["MediaControl"]), expect);
        Assert.Equal(Classification.WrongAction, strayTabs.Classification);
        Assert.Equal(Classification.WrongAction, media.Classification);
    }

    [Theory]
    [InlineData(OutcomeClass.Failed, Classification.ExecutionFailure)]
    [InlineData(OutcomeClass.Unsupported, Classification.Unsupported)]
    public void OutcomesOutsideTheAcceptedSetStillFail(OutcomeClass actual, Classification expected)
        => Assert.Equal(expected, ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(actual), OpenOrClarify).Classification);

    [Fact]
    public void FailedOrClarifyBothSafeForBackWithoutHistory()
    {
        var expect = new Expectation(ExpectedOutcome.Failed, NoMediaCommand: true,
            NewTabs: new NewTabsExpectation(Max: 0), AcceptedOutcomes: [ExpectedOutcome.Failed, ExpectedOutcome.Clarify]);
        Assert.Equal(Classification.Pass, ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Failed), expect).Classification);
        Assert.Equal(Classification.CorrectClarify, ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Clarify), expect).Classification);
        // A claimed success is never safe here.
        Assert.NotEqual(Classification.Pass, ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Complete), expect).Classification);
    }

    [Fact]
    public void ClarifyPreferredAcceptsACompletionWithoutStrayTabs()
    {
        // nav.ambiguous-code-editor: clarify preferred; focusing the one open editor is acceptable.
        var expect = new Expectation(ExpectedOutcome.Clarify, NewTabs: new NewTabsExpectation(Max: 0),
            AcceptedOutcomes: [ExpectedOutcome.Clarify, ExpectedOutcome.Complete]);
        Assert.Equal(Classification.Pass, ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Complete), expect).Classification);
        Assert.Equal(Classification.WrongAction,
            ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Complete, tabsCreated: 1), expect).Classification);
    }

    [Fact]
    public void SingleOutcomeSyntaxIsUnchanged()
    {
        var expect = new Expectation(ExpectedOutcome.Complete);
        Assert.Equal(Classification.UnnecessaryClarify,
            ScenarioEvaluator.EvaluateTurn(TestHelpers.Turn(OutcomeClass.Clarify), expect).Classification);
    }

    // ── Correctness vs efficiency ──

    private static readonly TurnRecord ReachedSlowly = TestHelpers.Turn(OutcomeClass.Complete, CommandRoute.ComputerUse,
        browserDecisions: 4, tabsCreated: 1, final: TestHelpers.Snapshot(activeUrl: "https://weather.com/"));

    [Fact]
    public void BudgetMissIsPartialUnlessEfficiencyIsAdvisory()
    {
        var strict = new Expectation(ExpectedOutcome.Complete, [CommandRoute.ComputerUse], MaxBrowserDecisions: 2,
            Final: new FinalExpectation(ActiveTabOriginContains: "weather.com"));
        Assert.Equal(Classification.Partial, ScenarioEvaluator.EvaluateTurn(ReachedSlowly, strict).Classification);

        var advisory = ScenarioEvaluator.EvaluateTurn(ReachedSlowly, strict with { EfficiencyAdvisory = true });
        Assert.Equal(Classification.Pass, advisory.Classification);
        Assert.Equal(["maxBrowserDecisions"], advisory.EfficiencyMisses);
    }

    [Fact]
    public void AdvisoryEfficiencyNeverExcusesAWrongEndState()
    {
        var expect = new Expectation(ExpectedOutcome.Complete, MaxBrowserDecisions: 2, EfficiencyAdvisory: true,
            Final: new FinalExpectation(ActiveTabOriginContains: "weather.gov"));
        Assert.NotEqual(Classification.Pass, ScenarioEvaluator.EvaluateTurn(ReachedSlowly, expect).Classification);
    }

    [Theory]
    [InlineData(BrowserScopeKind.ActiveTab, 0)]
    [InlineData(BrowserScopeKind.NewTaskTab, 1)]
    public void LessPreferredCorrectScopeIsAnEfficiencyMiss(BrowserScopeKind actual, int misses)
    {
        var expect = new Expectation(ExpectedOutcome.Complete,
            Scope: [ScopeExpectation.ActiveTab, ScopeExpectation.NewTaskTab], PreferredScope: [ScopeExpectation.ActiveTab],
            Final: new FinalExpectation(ActiveTabUrlContains: "library/json.html"));
        var turn = TestHelpers.Turn(OutcomeClass.Complete, scopeKind: ExecutionScopeKind.Browser, browserScopeKind: actual,
            final: TestHelpers.Snapshot(activeUrl: "https://docs.python.org/3/library/json.html"));
        var result = ScenarioEvaluator.EvaluateTurn(turn, expect);
        Assert.Equal(Classification.Pass, result.Classification);
        Assert.Equal(misses, result.EfficiencyMisses.Count);
    }

    [Fact]
    public void AggregatorCountsSuccessfulTurnsWithEfficiencyMisses()
    {
        var turn = ScenarioEvaluator.EvaluateTurn(ReachedSlowly, new Expectation(ExpectedOutcome.Complete,
            MaxBrowserDecisions: 2, EfficiencyAdvisory: true));
        var result = new ScenarioResult("r", "dest.explicit-domain", "n", "f", [], 1, DateTimeOffset.UtcNow, 1,
            turn.Classification, true, [], [], [], [turn], null);
        Assert.Equal(1, EvalAggregator.Summarize([result]).Efficiency.SuccessfulTurnsWithEfficiencyMisses);
    }
}
