namespace VoiceOS.Core.Browser;

/// <summary>
/// Minimal browser framing. The utterance remains authoritative; these fields are
/// deterministic hints and never a substitute semantic plan.
/// </summary>
public sealed record BrowserGoal(
    string OriginalUtterance,
    Uri? ExplicitUrl = null,
    string? NamedServiceHint = null,
    BrowserGoalNormalization? Normalization = null,
    Uri? ScopedDestination = null)
{
    public static BrowserGoal FromUtterance(string utterance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(utterance);
        return new(utterance.Trim());
    }

    internal static string BootstrapUrl(BrowserGoal goal)
        => goal.ExplicitUrl?.AbsoluteUri
           ?? goal.ScopedDestination?.AbsoluteUri
           ?? (ServiceResolver.Resolve(goal.NamedServiceHint)
               ?? ServiceResolver.Resolve(goal.Normalization?.PreferredService))?.WebOrigin.AbsoluteUri
           ?? "https://www.google.com/";

}

public sealed record BrowserTextCandidate(string Id, string Text);

public sealed record BrowserCorrectedTerm(string Heard, string Interpreted, double Confidence);

public sealed record BrowserGoalNormalization(
    string Objective, string? Entity, string? ResourceType, string? PreferredService,
    string? PreferredServiceUrl, IReadOnlyList<string> SearchQueries,
    string CompletionHint, IReadOnlyList<BrowserCorrectedTerm> CorrectedTerms,
    SemanticEndState EndState = SemanticEndState.OtherBoundedGoal);

/// <summary>Produces a small set of exact substrings; it never generates prose.</summary>
public static class BrowserTextCandidates
{
    private const int Limit = 6;
    public static IReadOnlyList<BrowserTextCandidate> From(BrowserGoal goal)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value)
        {
            value = value.Trim(' ', '"', '\'', '.', ',', ';', '!', '?');
            if (value.Length is > 0 and <= 240 && seen.Add(value))
                values.Add(value);
        }

        if (goal.Normalization is { } normalized)
        {
            foreach (var query in normalized.SearchQueries.Take(3)) Add(query);
            if (normalized.Entity is not null) Add(normalized.Entity);
            return values.Select((text, index) => new BrowserTextCandidate($"t{index + 1}", text)).ToArray();
        }

        return [];
    }
}
