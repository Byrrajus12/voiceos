using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>Validates the real shipped smoke.json (copied to this project's own output) loads and
/// is well-formed — a guard against the shipped scenario file silently rotting.</summary>
public class SmokeScenarioFileTests
{
    [Fact]
    public void SmokeScenariosLoadAndAreWellFormed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scenarios", "smoke.json");
        Assert.True(File.Exists(path), $"expected {path} to exist (copied from VoiceOS.Eval/Scenarios)");

        var scenarios = ScenarioLoader.LoadFile(path);
        Assert.Equal(2, scenarios.Count);

        var direct = Assert.Single(scenarios, s => s.Id == "smoke.direct.switch-to-notepad");
        Assert.Equal("direct-windows", direct.Family);
        Assert.True(direct.Safety.Unattended);
        Assert.False(direct.Safety.MutatesExternalState);
        Assert.Single(direct.Turns);
        Assert.Equal(ExpectedOutcome.Complete, direct.Turns[0].Expect!.Outcome);

        var browser = Assert.Single(scenarios, s => s.Id == "smoke.browser.open-wikipedia");
        Assert.Equal("browser-destination", browser.Family);
        Assert.Contains(ScopeExpectation.NewTaskTab, browser.Turns[0].Expect!.Scope!);
        Assert.Single(browser.Cleanup);
        Assert.Equal(SetupStepKind.CloseNewTabs, browser.Cleanup[0].Kind);
    }
}
