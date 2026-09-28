using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>
/// multi.restaurant-menu: Shake Shack's menu opens in place (URL stays https://shakeshack.com/#/),
/// so the end state is asserted through the last activated control, not the URL. Activity
/// sequences mirror the recorded physical runs.
/// </summary>
public class SpaEndStateTests
{
    private static readonly Expectation MenuExpectation = ScenarioLoader
        .LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Scenarios"))
        .Single(static s => s.Id == "multi.restaurant-menu").Turns.Single().Expect!;

    private static readonly BrowserActivityInfo[] ToHomepage =
    [
        new(null, null, null, true, null, "Shake Shack menu"),
        new("SetText", "combobox", "Search", false, null, "Shake Shack menu"),
        new("Activate", "button", "Google Search", false, null, null),
        new("Activate", "link", "Shake Shack: Home Page Shake Shack https://shakeshack.com", false, null, null)
    ];

    [Fact]
    public void ScenarioNoLongerRequiresMenuInTheUrl_ButStillRequiresASiteAndAMenuActivation()
    {
        Assert.Null(MenuExpectation.Final!.ActiveTabUrlContains);
        Assert.Equal("shakeshack.com", MenuExpectation.Final.ActiveTabOriginContains);
        Assert.Equal("menu", MenuExpectation.Final.LastActivatedTargetContains);
    }

    [Fact]
    public void CompletedAfterOpeningTheMenuInPlace_Passes()
    {
        var turn = Turn([.. ToHomepage, new("Activate", "button", "Open menu categories", false, null, null)]);
        Assert.Equal(Classification.Pass, ScenarioEvaluator.EvaluateTurn(turn, MenuExpectation).Classification);
    }

    [Fact]
    public void CompletedOnTheHomepageAlone_Fails()
    {
        var result = ScenarioEvaluator.EvaluateTurn(Turn(ToHomepage), MenuExpectation);
        Assert.DoesNotContain(result.Classification, ScenarioResult.SuccessSet);
        Assert.Contains(result.Checks, static check => check.Name == "final.lastActivatedTargetContains" && !check.Passed);
    }

    [Fact]
    public void AnUnrelatedInPageClickAfterLanding_StillFails()
    {
        var turn = Turn([.. ToHomepage, new("Activate", "button", "Accept cookies", false, null, null)]);
        Assert.DoesNotContain(ScenarioEvaluator.EvaluateTurn(turn, MenuExpectation).Classification, ScenarioResult.SuccessSet);
    }

    [Fact]
    public void MenuActivationFollowedByAnotherClick_JudgesTheLastActivation()
    {
        var turn = Turn([.. ToHomepage, new("Activate", "button", "Open menu categories", false, null, null),
            new("Activate", "link", "Find a location", false, null, null)]);
        Assert.DoesNotContain(ScenarioEvaluator.EvaluateTurn(turn, MenuExpectation).Classification, ScenarioResult.SuccessSet);
    }

    private static TurnRecord Turn(BrowserActivityInfo[] activities)
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, tabsCreated: 1, actions: activities.Length - 1,
            final: TestHelpers.Snapshot("chrome", "Shake Shack", "https://shakeshack.com/#/", "Shake Shack"));
        return turn with { Execution = turn.Execution with { BrowserActivities = activities } };
    }
}
