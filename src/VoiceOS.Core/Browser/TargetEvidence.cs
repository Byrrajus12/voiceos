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

    // Words that may sit beside a number in a label without changing what the number is (a page number, a step).
    private static readonly HashSet<string> NumberCompanions = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "p", "pg", "go", "to", "step", "tab", "slide", "no", "number", "of"
    };

    private static readonly System.Text.RegularExpressions.Regex Duration =
        new(@"\d:\d\d", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PagingHref =
        new(@"[?&](page|p|pg|pagenum|pageindex|paged)=(\d+)|/page/(\d+)|/p/(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether this control carries the query's evidence: its position when an ordinal was named; else every number
    /// named and, when there are no numbers, at least half of the distinctive words. A number alone is not evidence:
    /// a duration (4:08) or a count (4 reviews) contains it, a page control IS it.
    /// </summary>
    /// <param name="siblings">The other controls offered with this one; a run of bare numbers among them is collection ordering.</param>
    public static bool Matches(Query query, EvidenceElement element, IReadOnlyList<EvidenceElement>? siblings = null)
    {
        if (query.Ordinal is { } ordinal) return element.Position == ordinal;
        var labelTokens = BrowserCompletionEvidence.Tokens(element.Name).Select(Singular).ToArray();
        var words = labelTokens.Concat(BrowserCompletionEvidence.Tokens(PathWords(element.Href)).Select(Singular)).ToHashSet();
        var termsMatched = query.Terms.Count > 0 && query.Terms.Count(words.Contains) >= (query.Terms.Count + 1) / 2;
        if (query.Numbers.Count > 0)
        {
            if (!query.Numbers.All(labelTokens.Contains) || Duration.IsMatch(element.Name ?? "")) return false;
            // The label is the number (optionally with a page-like word), or it also carries the words that were described.
            var terse = labelTokens.Where(t => !query.Numbers.Contains(t)).All(NumberCompanions.Contains);
            if (!terse) return termsMatched && labelTokens.Length <= 8;
            // A bare number needs navigation structure around it; "page 4" in the label is that structure already.
            var named = labelTokens.Any(static t => t is "page" or "p" or "pg");
            return named || termsMatched || IsPagingStructure(query, element, siblings);
        }
        return termsMatched;
    }

    private static bool IsPagingStructure(Query query, EvidenceElement element, IReadOnlyList<EvidenceElement>? siblings)
    {
        if (element.Landmark is "navigation" || element.Kind == BrowserDomFacts.NavigationLink) return true;
        if (PagingHref.Match(element.Href ?? "") is { Success: true } m
            && new[] { m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value }.Any(v => v.Length > 0 && query.Numbers.Contains(v.TrimStart('0'))))
            return true;
        // Two or more other bare-number controls: a numbered series (1 2 3 ... 10) is how pagination lays out.
        return siblings is not null && siblings.Count(o => o.Id != element.Id && o.Enabled
            && BrowserCompletionEvidence.Tokens(o.Name).ToArray() is { Length: 1 } one && one[0].All(char.IsDigit)) >= 2;
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
        return list.Any(e => Matches(query, e, list));
    }

    /// <summary>
    /// A binding is established enough to act on when it is strong, or moderate and the control itself corroborates it
    /// (its words, numbers or position). A weak binding never is.
    /// </summary>
    public static bool Established(TargetBinding? binding, EvidenceElement? element, string? stepText, ProofThresholds thresholds,
        IReadOnlyList<EvidenceElement>? siblings = null)
    {
        if (binding is null || element is null) return false;
        return thresholds.Classify(binding) switch
        {
            BindingStrength.Strong => true,
            BindingStrength.Moderate => From(stepText) is { Informative: true } query && Matches(query, element, siblings),
            _ => false
        };
    }

    /// <summary>A bare clock reading (4:08) that labels a duration, never a target.</summary>
    internal static bool IsTimestamp(string? text) => text is not null && ClockLabel.IsMatch(text.Trim());

    // A duration badge, optionally with the playback state the page words beside it ("6:07 Now playing"): never a thing to choose.
    private static readonly System.Text.RegularExpressions.Regex ClockLabel =
        new(@"^\d{1,2}(:\d{2}){1,2}(\s+(now playing|watched|live|premiere))?$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string PathWords(string? href)
        => Uri.TryCreate(href, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : "";

    internal static string Singular(string term) => term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;
}
