using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

/// <summary>Structural guards for the shipped Alpha scenario suite. Pure file validation — no
/// product, model, or browser calls.</summary>
public class SuiteIntegrityTests
{
    private static readonly string[] Families =
    [
        "direct-windows", "media", "browser-destination", "browser-current-page", "tabs-context",
        "navigation-ambiguity", "browser-multistep", "sentinel"
    ];

    private static readonly IReadOnlyList<Scenario> All =
        ScenarioLoader.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "Scenarios"));

    private static IEnumerable<Scenario> Suite => All.Where(static s => !s.Tags.Contains("smoke"));

    [Fact]
    public void EveryScenarioIsExactlyOneOfGeneralizationSentinelOrSmoke()
    {
        foreach (var s in All)
        {
            var kinds = new[] { "generalization", "sentinel", "smoke" }.Count(s.Tags.Contains);
            Assert.True(kinds == 1, $"{s.Id} must carry exactly one of generalization/sentinel/smoke");
        }
    }

    [Fact]
    public void FamiliesAreKnownAndSentinelsAreIsolated()
    {
        foreach (var s in Suite)
        {
            Assert.Contains(s.Family, Families);
            Assert.Equal(s.Tags.Contains("sentinel"), s.Family == "sentinel");
            Assert.StartsWith(s.Family switch
            {
                "direct-windows" => "direct.", "media" => "media.", "browser-destination" => "dest.",
                "browser-current-page" => "page.", "tabs-context" => "tabs.",
                "navigation-ambiguity" => "nav.", "browser-multistep" => "multi.", _ => "sentinel."
            }, s.Id);
        }
    }

    [Fact]
    public void AllScenariosAreSafeForUnattendedLocalRuns()
        => Assert.All(All, static s => Assert.True(s.Safety.Unattended && !s.Safety.MutatesExternalState, s.Id));

    [Fact]
    public void BrowserStateScenariosRequireTheCompanion()
    {
        foreach (var s in Suite)
        {
            var needsBrowser = s.Cleanup.Any(static c => c.Kind == SetupStepKind.CloseNewTabs)
                || s.Turns.Any(static t => t.Expect?.Final is { } f
                    && (f.ActiveTabOriginContains ?? f.ActiveTabUrlContains ?? f.ActiveTabTitleContains) is not null);
            if (needsBrowser && !s.Id.StartsWith("dest.installed-web-app", StringComparison.Ordinal))
                Assert.True(s.Preconditions.Any(static p => p.Kind == PreconditionKind.CompanionConnected),
                    $"{s.Id} observes browser tabs but does not require the Companion");
        }
    }

    [Fact]
    public void PageSetupIsVerifiedBeforeTheTurn()
    {
        foreach (var s in Suite.Where(static s => s.Setup.Any(static x => x.File == "chrome.exe")))
            Assert.True(s.Preconditions.Any(static p => p.Kind == PreconditionKind.ActiveTabOrigin),
                $"{s.Id} opens a page in setup but never checks it is the active tab");
    }

    [Fact]
    public void MultiTurnTagMatchesTurnCount()
        => Assert.All(All, static s => Assert.Equal(s.Turns.Count > 1, s.Tags.Contains("multi-turn")));

    [Fact]
    public void SuiteHasExpectedShape()
    {
        var generalization = All.Count(static s => s.Tags.Contains("generalization"));
        var sentinels = All.Count(static s => s.Tags.Contains("sentinel"));
        Assert.InRange(generalization, 60, 80);
        Assert.InRange(sentinels, 6, 8);
        Assert.True(All.Count(static s => s.Turns.Count > 1) >= 8);
        Assert.Equal(All.Count, All.Select(static s => s.Turns[0].Transcript).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
