namespace VoiceOS.Eval.Live;

public sealed record RateInfo(int Attempts, int Successes, double Rate);

/// <summary>count/p50/p90/p95/mean over a sample, using linear-interpolation percentiles
/// (the "nearest rank via interpolated index" method: index = (n-1)*p, interpolating between
/// the two bracketing sorted samples).</summary>
public sealed record Percentiles(int Count, double P50, double P90, double P95, double Mean)
{
    public static Percentiles Of(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new(0, 0, 0, 0, 0);
        var sorted = values.OrderBy(v => v).ToArray();
        return new(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.90), Percentile(sorted, 0.95),
            sorted.Average());
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1) return sorted[0];
        var index = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper) return sorted[lower];
        var fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}

public sealed record EfficiencyInfo(double MeanActionsPerSuccess, double MeanBrowserDecisionsPerAttempt,
    double MeanModelStagesPerAttempt, int TotalTabsCreated, int NewTabsCheckFailures,
    int SuccessfulTurnsWithEfficiencyMisses = 0);

public sealed record ScenarioReliability(string ScenarioId, int Attempts, int Successes);

public sealed record EvalSummary(
    int Attempts, int Successes, double SuccessRate,
    IReadOnlyDictionary<string, RateInfo> PerFamily,
    IReadOnlyDictionary<Classification, int> ClassificationCounts,
    IReadOnlyDictionary<Classification, double> ClassificationRates,
    double FalseSuccessRate, double UnnecessaryClarifyRate, double WrongRouteRate, double WrongScopeRate,
    double WrongTargetRate, EfficiencyInfo Efficiency,
    Percentiles TotalPostSttMs, Percentiles FirstActionMs,
    IReadOnlyDictionary<string, Percentiles> PerStageMs,
    IReadOnlyList<ScenarioReliability> Reliability)
{
    public int InfrastructureUnavailable => ClassificationCounts.GetValueOrDefault(Classification.InfrastructureUnavailable);
    public bool ComparisonValid => InfrastructureUnavailable <= 3;
}

/// <summary>Pure aggregation over already-produced ScenarioResults. No I/O, no product calls.</summary>
public static class EvalAggregator
{
    public static EvalSummary Summarize(IReadOnlyList<ScenarioResult> results)
    {
        var counted = results.Where(r => r.Classification != Classification.Skipped).ToArray();
        var attempts = counted.Length;
        var successes = counted.Count(r => r.Success);
        var successRate = Rate(successes, attempts);

        var perFamily = counted.GroupBy(r => r.Family).ToDictionary(g => g.Key,
            g => new RateInfo(g.Count(), g.Count(r => r.Success), Rate(g.Count(r => r.Success), g.Count())));

        var classificationCounts = counted.GroupBy(r => r.Classification)
            .ToDictionary(g => g.Key, g => g.Count());
        var classificationRates = classificationCounts.ToDictionary(
            kv => kv.Key, kv => Rate(kv.Value, attempts));

        double RateOf(Classification c) => Rate(classificationCounts.GetValueOrDefault(c), attempts);

        var allTurns = counted.SelectMany(r => r.Turns).ToArray();
        var successfulTurns = counted.Where(r => r.Success).SelectMany(r => r.Turns).ToArray();
        var efficiency = new EfficiencyInfo(
            Mean(successfulTurns.Select(t => (double)t.Counts.Actions)),
            Mean(allTurns.Select(t => (double)t.Counts.BrowserDecisions)),
            Mean(allTurns.Select(t => (double)t.Counts.ModelStages)),
            allTurns.Sum(t => t.Counts.TabsCreated),
            allTurns.SelectMany(t => t.Checks).Count(c => c.Name == "newTabs" && !c.Passed),
            successfulTurns.Count(t => t.EfficiencyMisses.Count > 0));

        var totalPostStt = Percentiles.Of(allTurns.Select(t => t.Latency.TotalPostSttMs).ToArray());
        var firstAction = Percentiles.Of(allTurns.Where(t => t.Latency.FirstActionMs is not null)
            .Select(t => t.Latency.FirstActionMs!.Value).ToArray());
        var perStage = allTurns.SelectMany(t => t.Latency.StageTotals)
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => Percentiles.Of(g.Select(kv => kv.Value).ToArray()));

        var reliability = counted.GroupBy(r => r.ScenarioId)
            .Select(g => new ScenarioReliability(g.Key, g.Count(), g.Count(r => r.Success)))
            .OrderBy(r => r.ScenarioId, StringComparer.Ordinal).ToArray();

        return new EvalSummary(attempts, successes, successRate, perFamily, classificationCounts, classificationRates,
            RateOf(Classification.FalseSuccess), RateOf(Classification.UnnecessaryClarify),
            RateOf(Classification.WrongRoute), RateOf(Classification.WrongScope), RateOf(Classification.WrongTarget),
            efficiency, totalPostStt, firstAction, perStage, reliability);
    }

    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
    private static double Mean(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? 0 : array.Average();
    }
}
