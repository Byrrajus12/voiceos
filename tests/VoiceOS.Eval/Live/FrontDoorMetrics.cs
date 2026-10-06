namespace VoiceOS.Eval.Live;

public sealed record CallMetrics(int Turns, int Calls, double MeanCalls, double MeanSequentialHops,
    Percentiles TotalPostSttMs, Percentiles FrontDoorMs);
public sealed record CalibrationBucket(int Decile, int Count, int SuccessfulTurns);
public sealed record HeadMetrics(int Count, int DistributionsAbsent, IReadOnlyList<int> TopPHistogram,
    IReadOnlyList<int> MarginHistogram, double? PassedMeanP, double? FailedMeanP,
    double? PassedMeanMargin, double? FailedMeanMargin, IReadOnlyList<CalibrationBucket> Reliability);
public sealed record ClarifyDiagnostic(string ScenarioId, int Attempt, string Transcript,
    string? RoutingReason, string? ScopeDetail, IReadOnlyList<string> RescueRefusals);
public sealed record RescueDiagnostic(string ScenarioId, int Attempt, string Transcript,
    string Verdict, Classification Classification, bool BrowserExpected, IReadOnlyList<string> ProgramSteps);
public sealed record FrontDoorSummary(int PreExecUnnecessaryClarify, int PostActionUnnecessaryClarify,
    int RescueFired, int RescuePassed, int ContextualDirectChoices, int UnsafeBrowserRescues,
    IReadOnlyDictionary<string, int> RefusedRescueReasons, int SpeculativeStarted, int SpeculativeUsed,
    int SpeculativeUnused, double SpeculativeWastePercent, Percentiles SpeculativeDurationMs,
    IReadOnlyDictionary<string, CallMetrics> ByFamily, IReadOnlyDictionary<string, CallMetrics> ByLane,
    IReadOnlyDictionary<string, HeadMetrics> Heads, IReadOnlyList<ClarifyDiagnostic> RemainingPreExecClarifies,
    IReadOnlyList<RescueDiagnostic> Rescues);

/// <summary>Observational calibration: successful turn is a proxy for head correctness, not a labelled head truth.</summary>
public static class FrontDoorMetrics
{
    public static FrontDoorSummary Summarize(IReadOnlyList<ScenarioResult> results)
    {
        var rows = results.SelectMany(r => r.Turns.Select(t => (Result: r, Turn: t))).ToArray();
        var turns = rows.Select(x => x.Turn).ToArray();
        static bool Pre(TurnRecord t) => t.Classification == Classification.UnnecessaryClarify
            && t.Counts.Actions == 0 && t.Execution.Browser is null;
        static bool Fired(TurnRecord t) => t.FrontDoor?.Verdict is "RescueDirect" or "ContextualDirect";
        static bool Passed(TurnRecord t) => ScenarioResult.SuccessSet.Contains(t.Classification);
        // Older exports did not mark scenarios expressed only through a browser end-state check.
        static bool BrowserExpected(TurnRecord t) => t.BrowserExpected
            || t.Checks.Any(c => c.Name.StartsWith("final.activeTab", StringComparison.Ordinal));
        static string[] Refusals(TurnRecord t) => (t.FrontDoor?.Evaluations ?? [])
            .Where(e => e.Kind == VoiceOS.Core.Activation.FrontDoorVerdictKind.UseRoute)
            .SelectMany(e => e.Reasons).Distinct().ToArray();
        var speculative = turns.Where(t => t.FrontDoor?.SpeculativeStarted == true).ToArray();
        var unused = speculative.Count(t => t.FrontDoor?.SpeculativeUsed != true);
        var heads = rows.SelectMany(x => x.Turn.Heads.Select(h => (Head: h, Pass: Passed(x.Turn))))
            .GroupBy(x => x.Head.Stage + "/" + x.Head.Head).ToDictionary(g => g.Key, g =>
            {
                var items = g.ToArray();
                return new HeadMetrics(items.Length, items.Count(x => x.Head.P is null),
                    Histogram(items.Select(x => x.Head.P)), Histogram(items.Select(x => x.Head.Margin)),
                    Mean(items.Where(x => x.Pass).Select(x => x.Head.P)),
                    Mean(items.Where(x => !x.Pass).Select(x => x.Head.P)),
                    Mean(items.Where(x => x.Pass).Select(x => x.Head.Margin)),
                    Mean(items.Where(x => !x.Pass).Select(x => x.Head.Margin)),
                    items.Where(x => x.Head.P is not null).GroupBy(x => Bucket(x.Head.P!.Value))
                        .OrderBy(x => x.Key).Select(x => new CalibrationBucket(x.Key, x.Count(), x.Count(y => y.Pass))).ToArray());
            });
        return new(turns.Count(Pre), turns.Count(t => t.Classification == Classification.UnnecessaryClarify && !Pre(t)),
            turns.Count(Fired), turns.Count(t => Fired(t) && Passed(t)),
            turns.Count(t => t.FrontDoor?.Verdict == "ContextualDirect"),
            turns.Count(t => Fired(t) && BrowserExpected(t) && t.Classification is Classification.WrongRoute
                or Classification.WrongAction or Classification.FalseSuccess),
            turns.SelectMany(Refusals).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
            speculative.Length, speculative.Length - unused, unused,
            speculative.Length == 0 ? 0 : 100.0 * unused / speculative.Length,
            Percentiles.Of(speculative.Where(t => t.FrontDoor!.SpeculativeDurationMs is not null)
                .Select(t => t.FrontDoor!.SpeculativeDurationMs!.Value).ToArray()),
            rows.GroupBy(x => x.Result.Family).ToDictionary(g => g.Key, g => Calls(g.Select(x => x.Turn).ToArray())),
            turns.GroupBy(t => t.Lane).ToDictionary(g => g.Key, g => Calls(g.ToArray())), heads,
            rows.Where(x => Pre(x.Turn)).Select(x => new ClarifyDiagnostic(x.Result.ScenarioId, x.Result.Attempt,
                x.Turn.Transcript, x.Turn.InitialRoute?.Reason.ToString(), x.Turn.Scope?.Detail, Refusals(x.Turn))).ToArray(),
            rows.Where(x => Fired(x.Turn)).Select(x => new RescueDiagnostic(x.Result.ScenarioId, x.Result.Attempt,
                x.Turn.Transcript, x.Turn.FrontDoor!.Verdict, x.Turn.Classification, BrowserExpected(x.Turn),
                x.Turn.Execution.ProgramSteps)).ToArray());
    }

    private static CallMetrics Calls(TurnRecord[] turns) => new(turns.Length, turns.Sum(t => t.Counts.ModelStages),
        turns.Average(t => t.Counts.ModelStages), turns.Average(t => t.Counts.SequentialModelHops
            ?? t.Latency.Stages.Count(s => s.Name is "route" or "direct_decision" or "normalization"
                or "browser_decision" or "text_value" or "completion_confirmation")),
        Percentiles.Of(turns.Select(t => t.Latency.TotalPostSttMs).ToArray()),
        Percentiles.Of(turns.Where(t => t.Latency.StageTotals.ContainsKey("front_door"))
            .Select(t => t.Latency.StageTotals["front_door"]).ToArray()));
    private static int Bucket(double p) => Math.Clamp((int)(p * 10), 0, 9);
    private static int[] Histogram(IEnumerable<double?> values)
    {
        var bins = new int[10];
        foreach (var p in values) if (p is { } value) bins[Bucket(value)]++;
        return bins;
    }
    private static double? Mean(IEnumerable<double?> values)
    {
        var items = values.Where(x => x is not null).Select(x => x!.Value).ToArray();
        return items.Length == 0 ? null : items.Average();
    }
}
