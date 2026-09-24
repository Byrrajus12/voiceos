using System.Text.RegularExpressions;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Minimal browser framing. The utterance remains authoritative; these fields are
/// deterministic hints and never a substitute semantic plan.
/// </summary>
public sealed record BrowserGoal(
    string OriginalUtterance,
    Uri? ExplicitUrl = null,
    string? NamedServiceHint = null,
    bool ExplicitCurrentTabIntent = false,
    bool NearMe = false,
    BrowserGoalNormalization? Normalization = null)
{
    public static BrowserGoal FromUtterance(string utterance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(utterance);
        var trimmed = utterance.Trim();
        return new(
            trimmed,
            FindExplicitUrl(trimmed),
            FindServiceHint(trimmed),
            Regex.IsMatch(trimmed, @"\b(this|current)\s+tab\b", RegexOptions.IgnoreCase),
            Regex.IsMatch(trimmed, @"\bnear\s+me\b", RegexOptions.IgnoreCase));
    }

    internal static string BootstrapUrl(BrowserGoal goal)
        => goal.ExplicitUrl?.AbsoluteUri
           ?? (ServiceResolver.Resolve(goal.NamedServiceHint)
               ?? ServiceResolver.Resolve(goal.Normalization?.PreferredService))?.WebOrigin.AbsoluteUri
           ?? "https://www.google.com/";

    private static Uri? FindExplicitUrl(string utterance)
    {
        foreach (var token in utterance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (Uri.TryCreate(token.Trim('"', '\'', ',', '.', ';', ')', ']'), UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
                return uri;
        return null;
    }

    private static string? FindServiceHint(string utterance)
    {
        var search = Regex.Match(utterance, @"\bsearch\s+(?<service>.+?)\s+for\b", RegexOptions.IgnoreCase);
        if (search.Success)
            return Clean(search.Groups["service"].Value);
        var on = Regex.Match(utterance, @"\bon\s+(?<service>[^,.;]+?)\s*$", RegexOptions.IgnoreCase);
        return on.Success ? Clean(on.Groups["service"].Value) : null;
    }

    private static string? Clean(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim(' ', '"', '\'', '.', ',', ';');
}

public sealed record BrowserTextCandidate(string Id, string Text);

public sealed record BrowserCorrectedTerm(string Heard, string Interpreted, double Confidence);

public sealed record BrowserGoalNormalization(
    string Objective, string? Entity, string? ResourceType, string? PreferredService,
    string? PreferredServiceUrl, IReadOnlyList<string> SearchQueries,
    string CompletionHint, IReadOnlyList<BrowserCorrectedTerm> CorrectedTerms);

public static class BrowserGrounding
{
    // Only skip when the whole request is one literal interaction with a grounded end state.
    // The service has no observed page before it opens its task tab; an observation can be
    // supplied here when a later browser-context pass makes one available.
    public static bool IsSufficient(BrowserGoal goal, out string reason,
        InteractionObservation? observation = null)
    {
        var utterance = goal.OriginalUtterance;
        if (Regex.IsMatch(utterance, @"\b(?:and|then|so\s+that|in\s+order\s+to)\b", RegexOptions.IgnoreCase))
        {
            reason = "additional goal or constraint may remain after the literal action";
            return false;
        }

        if (goal.ExplicitUrl is not null && Regex.IsMatch(utterance,
            @"^\s*(?:please\s+)?(?:(?:open|go\s+to|navigate\s+to)\s+)?https?://\S+\s*$",
            RegexOptions.IgnoreCase))
        {
            reason = "single explicit destination";
            return true;
        }

        var ordinal = Regex.Match(utterance,
            @"^\s*(?:please\s+)?open\s+(?:the\s+)?(?<position>first|second|third)\s+one\s*[.!?]?\s*$",
            RegexOptions.IgnoreCase);
        if (ordinal.Success && observation is not null)
        {
            var position = ordinal.Groups["position"].Value.ToLowerInvariant() switch
            {
                "first" => 1, "second" => 2, _ => 3
            };
            var links = observation.Candidates.Count(candidate =>
                candidate.Label.StartsWith("link '", StringComparison.OrdinalIgnoreCase)
                && candidate.Actions.Any(action => action.Kind == InteractionActionKind.Activate));
            if (links >= position)
            {
                reason = "ordered visible link reference";
                return true;
            }
        }

        var plainSearch = Regex.Match(utterance,
            @"^\s*(?:please\s+)?search\s+(?:for\s+)?(?<payload>.+?)\s*[.!?]?\s*$",
            RegexOptions.IgnoreCase);
        if (plainSearch.Success && goal.NamedServiceHint is null
            && !Regex.IsMatch(plainSearch.Groups["payload"].Value,
                @"\b(on|in|at)\s+\S+|\b(repository|repo|profile|video|page|product)\b",
                RegexOptions.IgnoreCase))
        {
            reason = "single literal text interaction";
            return true;
        }
        reason = "end state or text requires semantic grounding";
        return false;
    }
}

/// <summary>Produces a small set of exact substrings; it never generates prose.</summary>
public static class BrowserTextCandidates
{
    private const int Limit = 6;
    private static readonly Regex[] UsefulPayloads =
    [
        new(@"\bsearch\s+.+?\s+for\s+(?<text>.+?)(?:\s+and\s+|$)", RegexOptions.IgnoreCase),
        new(@"\bplay\s+(?<text>.+?)\s+on\s+.+$", RegexOptions.IgnoreCase),
        new(@"\bfind\s+(?:the\s+)?(?<text>.+?)\s+on\s+.+$", RegexOptions.IgnoreCase),
        new(@"\b(?:search|find|play|open)\s+(?:for\s+)?(?<text>.+)$", RegexOptions.IgnoreCase)
    ];

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

        foreach (var pattern in UsefulPayloads)
        {
            var match = pattern.Match(goal.OriginalUtterance);
            if (match.Success)
                Add(match.Groups["text"].Value);
        }

        foreach (var value in values.ToArray())
        {
            var reduced = Regex.Replace(value, @"\b(repository|repo|video|page|profile)\b", "", RegexOptions.IgnoreCase);
            reduced = Regex.Replace(reduced, @"\s+", " ").Trim();
            Add(reduced);
        }

        Add(goal.OriginalUtterance);
        return values.Take(Limit).Select((text, index) => new BrowserTextCandidate($"t{index + 1}", text)).ToArray();
    }
}
