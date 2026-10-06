using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Decides what to do when the binder's belief is spread over several controls. Target identity is judged here from the
/// candidates' own words and what the user actually said, never from how confident the action model is about the operation
/// (CLICK=.99 says nothing about which item was meant).
/// </summary>
internal static class TargetAmbiguity
{
    /// <summary>A candidate needs at least this much merged belief to be offered to the user at all.</summary>
    internal const double ChoiceFloor = .25;
    /// <summary>When the best item leads the next by at least this much, it simply wins and nothing is asked.</summary>
    internal const double ChoiceMargin = .20;
    /// <summary>Below this a control is noise; above it a control may still be another handle on a plausible item.</summary>
    private const double MergeFloor = .05;
    /// <summary>A binder belief this strong may act without the item carrying the user's words (a synonym such as Sign in / Log in).</summary>
    private const double StrongBinder = .80;

    public enum Kind
    {
        /// <summary>Not ambiguous: the caller's ordinary binding applies.</summary>
        Clear,
        /// <summary>The user's own words or position single one item out: execute it.</summary>
        Winner,
        /// <summary>Several real items remain and only the user can say which.</summary>
        Ask,
        /// <summary>Genuinely ambiguous, but no honest question can be built from the page: a binding problem, not a choice.</summary>
        Unresolvable
    }

    public sealed record Verdict(Kind Kind, string? Ref = null, IReadOnlyList<ChoiceOption>? Options = null, string Why = "", string? Summary = null)
    {
        public static readonly Verdict Clear = new(Kind.Clear);
    }

    // Words that describe the category of the wanted item or the act, never which item it is.
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "matching", "match", "matches", "relevant", "video", "song", "track", "music", "result", "item", "link", "play", "watch",
        "listen", "best", "top", "good", "some", "any", "new", "latest", "popular", "first", "by", "from", "youtube"
    };

    private static readonly HashSet<string> GenericLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "option", "options", "choice", "item", "result", "here", "click here", "more", "read more", "learn more", "details",
        "view", "select", "continue", "submit", "ok", "yes", "no", "image", "icon", "link", "button", "unnamed"
    };

    private sealed class Item
    {
        public List<(EvidenceElement Element, double P)> Handles { get; } = [];
        public double P => Handles.Sum(static h => h.P);
        public EvidenceElement Best => Handles.OrderByDescending(static h => h.P).ThenByDescending(h => h.Element == Named).First().Element;
        public string? Name { get; set; }
        public EvidenceElement? Named { get; set; }
    }

    /// <summary>
    /// Decides over semantic items, not DOM nodes: every offered control is first grouped into the result it belongs to (same
    /// address, else same collection position under the same container). The binder's probability is then a score for an item
    /// (the sum over its handles). An item is eligible when the binder believes in it, or when it carries every word the user
    /// said (a real alternative the binder, which names one node, gave no mass). Candidates are bounded to <see cref="PendingChoice.MaxOptions"/>.
    /// </summary>
    public static Verdict Assess(InteractionObservation observation, IReadOnlyDictionary<string, double> probabilities,
        IReadOnlyCollection<string> clickable, string? stepText, string? utterance)
    {
        var ranked = probabilities.Where(p => clickable.Contains(p.Key)).OrderByDescending(static p => p.Value).ToArray();
        if (ranked.Length < 2 || probabilities.GetValueOrDefault("NONE") >= ranked[0].Value) return Verdict.Clear;
        var elements = BrowserEvidence.Elements(observation.Evidence).ToDictionary(static e => e.Id, StringComparer.Ordinal);
        var items = Merge(clickable.Where(elements.ContainsKey).Select(id => (elements[id], probabilities.GetValueOrDefault(id))).ToArray());
        var leader = items.OrderByDescending(static i => i.P).First();
        // The binder itself must believe some item is the target; many weak nodes are noise, not a question.
        if (leader.P < ChoiceFloor) return Verdict.Clear;

        var query = GroundedQuery(stepText, utterance);
        var total = query.Terms.Count + query.Numbers.Count;
        var leaderPlaced = leader.Handles.Any(static h => h.Element.Position is not null);
        var believed = items.Where(i => i.P >= ChoiceFloor).OrderByDescending(static i => i.P).ToList();
        var alternatives = query.Ordinal is null && total > 0
            ? items.Where(i => !believed.Contains(i) && i.Name is not null && Score(query, i) >= total
                && (!leaderPlaced || i.Handles.Any(static h => h.Element.Position is not null))).ToList()
            : [];
        var plausible = believed.Concat(alternatives).Take(PendingChoice.MaxOptions).ToList();
        var summary = string.Join(" ", plausible.Select((i, n) =>
            $"group{n + 1}=\"{i.Name ?? i.Best.Name}\" handles=[{string.Join(',', i.Handles.Where(h => h.P > 0 || i.Handles.Count == 1).Select(static h => h.Element.Id))}] p={i.P:F2} score={Score(query, i)}"));

        // Several handles on one item (thumbnail, title, nested node) are that one item, not a disagreement.
        // Being one item says what the handles ARE, not that the item is what the user asked for. With words of theirs to compare and
        // none of them on the item, a moderate binder belief alone is no grounds to act: it is absence of target evidence.
        if (plausible.Count == 1 && query.Ordinal is null && query.Terms.Count > 0 && Score(query, plausible[0]) == 0 && plausible[0].P < StrongBinder)
            return new(Kind.Unresolvable, Why: "no_target_evidence", Summary: summary);
        if (plausible.Count == 1)
            return plausible[0].Handles.Count > 1 && plausible[0].P - items.Where(i => i != plausible[0]).Select(static i => i.P).DefaultIfEmpty(0).Max() >= ChoiceMargin
                ? new(Kind.Winner, plausible[0].Best.Id, Why: "same_item", Summary: summary) : Verdict.Clear;
        // Without words of the user's to compare, a clear binder lead simply wins.
        if (query.Terms.Count == 0 && plausible[0].P - plausible[1].P >= ChoiceMargin) return Verdict.Clear;

        // What the user said can settle it: only a word they actually used (or a position) counts, and only when it tells the items apart.
        if (query.Ordinal is not null)
        {
            // A named position settles it only when the page reports positions and exactly one item holds it; otherwise it is not a preference to ask about.
            var holders = plausible.Where(i => i.Handles.Any(h => h.Element.Position == query.Ordinal)).ToArray();
            return holders.Length == 1 ? new(Kind.Winner, holders[0].Best.Id, Why: "ordinal", Summary: summary) : Verdict.Clear;
        }
        if (query.Informative)
        {
            var scores = plausible.Select(i => (Item: i, Score: Score(query, i))).ToArray();
            var top = scores.Max(static s => s.Score);
            // Nothing on offer carries any word the user said: that is the binder being confused, not a preference to ask about.
            if (top == 0 && query.Terms.Count > 0) return new(Kind.Unresolvable, Why: "no_candidate_matches_request", Summary: summary);
            if (top > 0 && scores.Count(s => s.Score == top) == 1) return new(Kind.Winner, scores.First(s => s.Score == top).Item.Best.Id, Why: "user_words", Summary: summary);
            if (top > 0) plausible = scores.Where(s => s.Score == top).Select(static s => s.Item).ToList();
        }

        if (plausible.Any(static i => i.Name is null)) return new(Kind.Unresolvable, Why: "option_without_words", Summary: summary);
        var options = new List<ChoiceOption>();
        foreach (var item in plausible)
        {
            var e = item.Named!;
            var twin = plausible.Count(o => Normalize(o.Name) == Normalize(item.Name)) > 1;
            var secondary = twin ? Distinguisher(e, item.Name!) : null;
            if (twin && (secondary is null || plausible.Count(o => o != item && Normalize(o.Name) == Normalize(item.Name)
                    && Distinguisher(o.Named!, o.Name!) == secondary) > 0))
                return new(Kind.Unresolvable, Why: "indistinguishable_options", Summary: summary);
            options.Add(new ChoiceOption(Guid.NewGuid().ToString("N")[..8], item.Best.Id,
                item.Name!, secondary, item.P, e.Href, e.Role, Squash(e.Context) is { Length: > 0 } ctx ? ctx : null, e.Position,
                item.Handles.Select(static h => Squash(h.Element.Name)).Where(n => n.Length > 0 && n != item.Name && !TargetEvidence.IsTimestamp(n))
                    .Distinct().Take(3).ToArray()));
        }
        return new(Kind.Ask, Options: options, Why: "unresolved_preference", Summary: summary);
    }

    /// <summary>The step's described target restricted to what the user said, and to words that can tell two items apart.</summary>
    internal static TargetEvidence.Query GroundedQuery(string? stepText, string? utterance)
    {
        var query = TargetEvidence.From(stepText);
        var said = utterance is null ? null
            : BrowserCompletionEvidence.Tokens(utterance).Select(TargetEvidence.Singular).ToHashSet();
        var terms = query.Terms.Where(t => !GenericWords.Contains(t) && (said is null || said.Contains(t))).ToArray();
        return new(terms, query.Numbers, query.Ordinal);
    }

    private static int Score(TargetEvidence.Query query, Item item)
    {
        if (query.Ordinal is { } ordinal) return item.Handles.Any(h => h.Element.Position == ordinal) ? 1 : 0;
        var words = item.Handles.SelectMany(h => BrowserCompletionEvidence.Tokens(h.Element.Name)
                .Concat(BrowserCompletionEvidence.Tokens(h.Element.Context))
                .Concat(BrowserCompletionEvidence.Tokens(TargetEvidence.PathWords(h.Element.Href))))
            .Select(TargetEvidence.Singular).ToHashSet();
        return query.Terms.Count(words.Contains) + query.Numbers.Count(words.Contains);
    }

    /// <summary>Groups controls that are one semantic result: the same address, or the same collection position under the same container.</summary>
    private static List<Item> Merge(IReadOnlyList<(EvidenceElement Element, double P)> candidates)
    {
        var items = new List<Item>();
        foreach (var (element, p) in candidates)
        {
            var existing = items.FirstOrDefault(i => i.Handles.Any(h => SameItem(h.Element, element)));
            if (existing is null) items.Add(existing = new Item());
            existing.Handles.Add((element, p));
        }
        foreach (var item in items)
        {
            foreach (var h in item.Handles.OrderByDescending(static h => h.P))
            {
                var text = Squash(!string.IsNullOrWhiteSpace(h.Element.Name) ? h.Element.Name : h.Element.Value);
                if (text.Length == 0 || text.Any(char.IsControl) || TargetEvidence.IsTimestamp(text) || GenericLabels.Contains(Normalize(text))) continue;
                item.Name = text.Length > 80 ? text[..80].TrimEnd() + "…" : text;
                item.Named = h.Element;
                break;
            }
        }
        return items;
    }

    private static bool SameItem(EvidenceElement a, EvidenceElement b)
    {
        if (a.Href is { Length: > 0 } && b.Href is { Length: > 0 })
            return CanonicalHref(a.Href) == CanonicalHref(b.Href);
        return a.Position is not null && a.Position == b.Position && Squash(a.Context).Length > 0
            && string.Equals(Squash(a.Context), Squash(b.Context), StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] TrackingParams = ["fbclid", "gclid", "msclkid", "igshid", "mc_cid", "mc_eid", "yclid"];

    /// <summary>
    /// The address with trivial differences removed: no fragment, lower-cased scheme and host, no default port, no trailing slash, query
    /// parameters in a fixed order, and only parameters known to be tracking metadata dropped. Anything else may define the resource.
    /// </summary>
    internal static string? CanonicalHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var text = href.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return (text.Contains('#') ? text[..text.IndexOf('#')] : text).TrimEnd('/').ToLowerInvariant();
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(q => q.Split('=')[0] is var k && !k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                && !TrackingParams.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(static q => q, StringComparer.Ordinal).ToArray();
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{path}{(query.Length > 0 ? "?" + string.Join('&', query) : "")}";
    }

    /// <summary>Which semantic result a control is a handle of: its canonical address, else its list position under its container, else itself.</summary>
    internal static string GroupKey(EvidenceElement e)
        => CanonicalHref(e.Href) is { } href ? "h:" + href
            : e.Position is not null && Squash(e.Context).Length > 0 ? $"p:{e.Position}|{Squash(e.Context).ToLowerInvariant()}" : "i:" + e.Id;

    private static string? Distinguisher(EvidenceElement element, string own)
    {
        var context = Squash(element.Context);
        if (context.Length > 0 && !context.Equals(own, StringComparison.OrdinalIgnoreCase))
            return context.Length > 60 ? context[..60].TrimEnd() + "…" : context;
        return Uri.TryCreate(element.Href, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath.TrimEnd('/') : null;
    }

    private static string Normalize(string? text) => string.Join(' ', BrowserCompletionEvidence.Tokens(text)
        .Where(static t => t is not ("the" or "a" or "an" or "tab" or "button" or "link" or "page")));

    private static string Squash(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
