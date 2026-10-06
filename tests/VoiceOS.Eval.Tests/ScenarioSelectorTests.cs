using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public class ScenarioSelectorTests
{
    private static Scenario Make(string id, string family, params string[] tags)
        => new(id, id, family, tags, new ScenarioSafety(), [], [], [], [new Turn("x")]);

    private static readonly Scenario[] Scenarios =
    [
        Make("smoke.direct.a", "direct-windows", "smoke", "direct"),
        Make("smoke.browser.b", "browser-destination", "smoke", "browser"),
        Make("other.c", "direct-windows", "risky"),
    ];

    [Fact]
    public void ExactIdMatches()
    {
        var result = ScenarioSelector.Select(Scenarios, ["smoke.direct.a"], [], [], false, false);
        Assert.Equal(["smoke.direct.a"], result.Selected.Select(s => s.Id));
    }

    [Fact]
    public void WildcardIdMatchesByPrefix()
    {
        var result = ScenarioSelector.Select(Scenarios, ["smoke.*"], [], [], false, false);
        Assert.Equal(2, result.Selected.Count);
    }

    [Fact]
    public void TagMatches()
    {
        var result = ScenarioSelector.Select(Scenarios, [], ["browser"], [], false, false);
        Assert.Equal(["smoke.browser.b"], result.Selected.Select(s => s.Id));
    }

    [Fact]
    public void FamilyMatches()
    {
        var result = ScenarioSelector.Select(Scenarios, [], [], ["direct-windows"], false, false);
        Assert.Equal(2, result.Selected.Count);
    }

    [Fact]
    public void AllSelectsEverythingRegardlessOfSelectors()
    {
        var result = ScenarioSelector.Select(Scenarios, [], [], [], true, false);
        Assert.Equal(3, result.Selected.Count);
    }

    [Fact]
    public void UnsafeScenariosAreSkippedUnlessIncludeUnsafe()
    {
        var unsafeScenario = new Scenario("unsafe.one", "n", "f", ["smoke"],
            new ScenarioSafety(Unattended: false), [], [], [], [new Turn("x")]);
        var scenarios = new[] { unsafeScenario };

        var excluded = ScenarioSelector.Select(scenarios, [], ["smoke"], [], false, includeUnsafe: false);
        Assert.Empty(excluded.Selected);
        Assert.Single(excluded.Skipped);

        var included = ScenarioSelector.Select(scenarios, [], ["smoke"], [], false, includeUnsafe: true);
        Assert.Single(included.Selected);
        Assert.Empty(included.Skipped);
    }

    [Fact]
    public void NoSelectorAndNoAllThrows()
    {
        Assert.Throws<InvalidOperationException>(() => ScenarioSelector.Select(Scenarios, [], [], [], false, false));
    }
}
