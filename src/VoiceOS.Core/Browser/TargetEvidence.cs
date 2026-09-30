using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Deterministic, generic evidence about whether a control could be the target a step describes: its own words
/// (label, href path), the numbers it carries, and its position inside a collection. No site knowledge.
/// </summary>
internal static class TargetEvidence
{
    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1, ["top"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
        ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10
    };

    // Words that say what to do, not which control: dropped so they cannot count as (or against) a match.
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "go", "goto", "navigate", "visit", "select", "choose", "press", "tap", "click", "open", "show", "find", "locate",
        "take", "bring", "get", "me", "to", "the", "a", "an", "of", "for", "and", "in", "on", "it", "that", "this", "its",
        "page", "link", "result", "results", "section", "item", "button", "one", "control", "site", "website"
    };

    public sealed record Query(IReadOnlyList<string> Terms, IReadOnlyList<string> Numbers, int? Ordinal)
    {
        /// <summary>The description says something a control's own words or position can be compared with.</summary>
        public bool Informative => Terms.Count + Numbers.Count > 0 || Ordinal is not null;
    }

    public static Query From(string? text)
    {
        var terms = new List<string>();
        var numbers = new List<string>();
        int? ordinal = null;
        foreach (var token in BrowserCompletionEvidence.Tokens(text))
        {
            if (Ordinals.TryGetValue(token, out var position)) ordinal = position;
            else if (token.All(char.IsDigit)) numbers.Add(token);
            else if (!Filler.Contains(token)) terms.Add(Singular(token));
        }
        return new(terms.Distinct().ToArray(), numbers.Distinct().ToArray(), ordinal);
    }

    /// <summary>
    /// Whether this control carries the query's evidence: its position when an ordinal was named; else every number
    /// named and, when there are no numbers, at least half of the distinctive words.
    /// </summary>
    public static bool Matches(Query query, EvidenceElement element)
    {
        if (query.Ordinal is { } ordinal) return element.Position == ordinal;
        var words = BrowserCompletionEvidence.Tokens(element.Name + " " + PathWords(element.Href)).Select(Singular).ToHashSet();
        if (query.Numbers.Count > 0) return query.Numbers.All(words.Contains);
        return query.Terms.Count > 0 && query.Terms.Count(words.Contains) >= (query.Terms.Count + 1) / 2;
    }

    /// <summary>
    /// False only when the query is informative and clearly no offered control matches. An ordinal on a page with no
    /// collection structure cannot be judged from code, so it is left to the binder.
    /// </summary>
    public static bool AnyPlausible(Query query, IEnumerable<EvidenceElement> controls)
    {
        if (!query.Informative) return true;
        var list = controls.ToArray();
        if (query.Ordinal is not null && !list.Any(static e => e.Position is not null)) return true;
        return list.Any(e => Matches(query, e));
    }

    /// <summary>
    /// A binding is established enough to act on when it is strong, or moderate and the control itself corroborates it
    /// (its words, numbers or position). A weak binding never is.
    /// </summary>
    public static bool Established(TargetBinding? binding, EvidenceElement? element, string? stepText, ProofThresholds thresholds)
    {
        if (binding is null || element is null) return false;
        return thresholds.Classify(binding) switch
        {
            BindingStrength.Strong => true,
            BindingStrength.Moderate => From(stepText) is { Informative: true } query && Matches(query, element),
            _ => false
        };
    }

    private static string PathWords(string? href)
        => Uri.TryCreate(href, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : "";

    private static string Singular(string term) => term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;
}
