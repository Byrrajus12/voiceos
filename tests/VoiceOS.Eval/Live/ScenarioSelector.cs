namespace VoiceOS.Eval.Live;

public sealed record SkippedScenario(Scenario Scenario, string Reason);

public sealed record SelectionResult(IReadOnlyList<Scenario> Selected, IReadOnlyList<SkippedScenario> Skipped);

/// <summary>Pure selection of scenarios by id/tag/family/all, with unsafe-scenario exclusion.</summary>
public static class ScenarioSelector
{
    public static SelectionResult Select(IReadOnlyList<Scenario> scenarios, IReadOnlyList<string> ids,
        IReadOnlyList<string> tags, IReadOnlyList<string> families, bool all, bool includeUnsafe)
    {
        if (!all && ids.Count == 0 && tags.Count == 0 && families.Count == 0)
            throw new InvalidOperationException(
                "No selector given: pass --scenario, --tag, --family, or --all (never runs everything implicitly).");

        var matched = all ? scenarios.ToList() : scenarios.Where(s => Matches(s, ids, tags, families)).ToList();
        var selected = new List<Scenario>();
        var skipped = new List<SkippedScenario>();
        foreach (var s in matched)
        {
            if (!includeUnsafe && (!s.Safety.Unattended || s.Safety.MutatesExternalState))
                skipped.Add(new(s, $"unsafe (unattended={s.Safety.Unattended}, mutatesExternalState={s.Safety.MutatesExternalState}); pass --include-unsafe to run it"));
            else
                selected.Add(s);
        }
        return new(selected, skipped);
    }

    private static bool Matches(Scenario s, IReadOnlyList<string> ids, IReadOnlyList<string> tags, IReadOnlyList<string> families)
    {
        if (ids.Any(id => MatchesId(s.Id, id))) return true;
        if (tags.Any(t => s.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return true;
        if (families.Any(f => string.Equals(s.Family, f, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static bool MatchesId(string scenarioId, string pattern)
        => pattern.EndsWith('*')
            ? scenarioId.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(scenarioId, pattern, StringComparison.Ordinal);
}
