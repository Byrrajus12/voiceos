using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public class EvalAggregatorTests
{
    private static ScenarioResult Result(string id, string family, Classification classification, params TurnRecord[] turns)
        => new("run1", id, id, family, [], 1, DateTimeOffset.UtcNow, 100, classification,
            ScenarioResult.SuccessSet.Contains(classification), [], [], [], turns, null);

    [Fact]
    public void SuccessRateCountsOnlySuccessClassifications()
    {
        var results = new[]
        {
            Result("a", "f1", Classification.Pass),
            Result("b", "f1", Classification.WrongRoute),
            Result("c", "f1", Classification.CorrectClarify),
        };
        var summary = EvalAggregator.Summarize(results);
        Assert.Equal(3, summary.Attempts);
        Assert.Equal(2, summary.Successes);
        Assert.Equal(2.0 / 3, summary.SuccessRate, 5);
    }

    [Fact]
    public void SkippedResultsAreExcludedFromRates()
    {
        var results = new[]
        {
            Result("a", "f1", Classification.Pass),
            Result("b", "f1", Classification.Skipped),
        };
        var summary = EvalAggregator.Summarize(results);
        Assert.Equal(1, summary.Attempts);
        Assert.Equal(1.0, summary.SuccessRate, 5);
    }

    [Fact]
    public void PerFamilyRatesComputedIndependently()
    {
        var results = new[]
        {
            Result("a", "family1", Classification.Pass),
            Result("b", "family1", Classification.WrongRoute),
            Result("c", "family2", Classification.Pass),
        };
        var summary = EvalAggregator.Summarize(results);
        Assert.Equal(0.5, summary.PerFamily["family1"].Rate, 5);
        Assert.Equal(1.0, summary.PerFamily["family2"].Rate, 5);
    }

    [Fact]
    public void PercentilesUseLinearInterpolation()
    {
        // Sorted [10,20,30,40]; p50 index = 0.5*3 = 1.5 -> interpolate between 20 and 30 => 25.
        var values = new double[] { 40, 10, 30, 20 };
        var percentiles = Percentiles.Of(values);
        Assert.Equal(4, percentiles.Count);
        Assert.Equal(25.0, percentiles.P50, 5);
        Assert.Equal(25.0, percentiles.Mean, 5);
    }

    [Fact]
    public void PercentilesOfEmptySampleAreZero()
    {
        var percentiles = Percentiles.Of([]);
        Assert.Equal(0, percentiles.Count);
        Assert.Equal(0, percentiles.P50);
    }

    [Fact]
    public void ClassificationRatesSumToAttemptsCoverage()
    {
        var results = new[]
        {
            Result("a", "f", Classification.Pass),
            Result("b", "f", Classification.WrongRoute),
            Result("c", "f", Classification.FalseSuccess),
        };
        var summary = EvalAggregator.Summarize(results);
        Assert.Equal(1.0 / 3, summary.ClassificationRates[Classification.WrongRoute], 5);
        Assert.Equal(1.0 / 3, summary.FalseSuccessRate, 5);
    }
}
