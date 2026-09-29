using VoiceOS.Core.Activation;
using VoiceOS.Core.Decision;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public sealed class FrontDoorMetricsTests
{
    private static ScenarioResult Result(string id, TurnRecord turn) => new("r", id, id, "browser-current-page",
        [], 1, DateTimeOffset.UtcNow, 0, turn.Classification,
        ScenarioResult.SuccessSet.Contains(turn.Classification), [], [], [], [turn], null);

    [Fact]
    public void SplitsClarificationAtTheDocumentedBoundaryIncludingZeroActionBrowserRuns()
    {
        var pre = TestHelpers.Turn(OutcomeClass.Clarify) with { Classification = Classification.UnnecessaryClarify };
        var post = pre with { Execution = pre.Execution with {
            Browser = new("Uncertain", "no proof", "https://example.org", "page", 1, 0, 1, 1) } };
        var afterDirect = pre with { Counts = pre.Counts with { Actions = 1 } };
        var summary = FrontDoorMetrics.Summarize([Result("pre", pre), Result("post", post), Result("direct", afterDirect)]);
        Assert.Equal(1, summary.PreExecUnnecessaryClarify);
        Assert.Equal(2, summary.PostActionUnnecessaryClarify);
        Assert.Equal("pre", Assert.Single(summary.RemainingPreExecClarifies).ScenarioId);
    }

    [Fact]
    public void SafeOfferIsNotAFiredRescueAndUnusedSpeculationStillCountsAsWaste()
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete, modelStages: 3) with {
            Classification = Classification.Pass,
            FrontDoor = new("UseRoute", ["safe_direct_offered"],
                [new("rescue", FrontDoorVerdictKind.UseRoute, ["web_wording", "media_guard"], "CloseWindow", true, .2, .8, 0)],
                true, false, 180), Counts = new(0, 0, 0, 0, 3, 0) { SequentialModelHops = 1 } };
        var summary = FrontDoorMetrics.Summarize([Result("offer", turn)]);
        Assert.Equal(0, summary.RescueFired);
        Assert.Equal(100, summary.SpeculativeWastePercent);
        Assert.Equal(1, summary.RefusedRescueReasons["web_wording"]);
        Assert.Equal(3, summary.ByFamily["browser-current-page"].MeanCalls);
        Assert.Equal(1, summary.ByFamily["browser-current-page"].MeanSequentialHops);
    }

    [Theory]
    [InlineData(Classification.WrongRoute)]
    [InlineData(Classification.WrongAction)]
    [InlineData(Classification.FalseSuccess)]
    public void ExposesEveryUnsafeBrowserRescue(Classification classification)
    {
        var turn = TestHelpers.Turn(OutcomeClass.Complete) with { Classification = classification,
            BrowserExpected = true, FrontDoor = new("ContextualDirect", [], [], true, true, 100) };
        var summary = FrontDoorMetrics.Summarize([Result("unsafe", turn)]);
        Assert.Equal(1, summary.UnsafeBrowserRescues);
        Assert.Equal(1, summary.ContextualDirectChoices);
        Assert.Equal(0, summary.RescuePassed);
        Assert.Equal(classification, Assert.Single(summary.Rescues).Classification);
    }

    [Fact]
    public void CalibrationRetainsConfidentFailureAbsentDistributionAndDecileEndpoints()
    {
        TurnRecord Turn(Classification c, double? p, double? margin) => TestHelpers.Turn(OutcomeClass.Complete) with {
            Classification = c, Heads = [new JevDiagnostics.SummaryRecord("route", "route", "COMPUTER_USE", p, "DIRECT_CAPABILITY", .1, margin, .99)] };
        var summary = FrontDoorMetrics.Summarize([Result("good", Turn(Classification.Pass, 1, .9)),
            Result("wrong", Turn(Classification.FalseSuccess, .95, .9)), Result("absent", Turn(Classification.Pass, null, null))]);
        var head = summary.Heads["route/route"];
        Assert.Equal(1, head.DistributionsAbsent);
        Assert.Equal(2, head.TopPHistogram[9]);
        Assert.Equal(.95, head.FailedMeanP);
        Assert.Equal(new CalibrationBucket(9, 2, 1), Assert.Single(head.Reliability));
    }
}
