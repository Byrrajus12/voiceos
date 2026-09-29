using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// The semantic relation a spoken reference has to earlier work, as judged by the chooser. Product code
/// never reads the words; it enforces the typed guarantees each relation implies.
/// </summary>
public enum ReferenceRelation { None, Same, Alternative, Previous, Property }

/// <summary>A validated, typed thing the reference could denote. Carries no page content.</summary>
public sealed record ReferentCandidate(string Id, Referent Referent, bool Current, string? Group, int Rank)
{
    public string Site => Uri.TryCreate(Referent.Url ?? Referent.Href, UriKind.Absolute, out var uri) ? uri.Host : "";
}

public enum ReferentChoiceKind { Selected, None, Ambiguous, Unavailable }

/// <summary>The focused semantic pick over typed candidates. It can only choose an offered id, NONE or AMBIGUOUS.</summary>
public sealed record ReferentChoice(ReferentChoiceKind Kind, string? CandidateId = null,
    ReferenceRelation Relation = ReferenceRelation.None);

public enum ReferentResolutionKind { Selected, None, Ambiguous, Unavailable }

public sealed record ReferentResolution(ReferentResolutionKind Kind, ReferentCandidate? Candidate = null, string? Reason = null)
{
    public static ReferentResolution Selected(ReferentCandidate candidate, string reason) => new(ReferentResolutionKind.Selected, candidate, reason);
    public static ReferentResolution NoneFound(string reason) => new(ReferentResolutionKind.None, Reason: reason);
    public static ReferentResolution Ambiguous(string reason) => new(ReferentResolutionKind.Ambiguous, Reason: reason);
    public static ReferentResolution Unavailable(string reason) => new(ReferentResolutionKind.Unavailable, Reason: reason);
}

/// <summary>
/// Deterministic-first resolution of "which previously established thing does the user mean". Candidates
/// are only validated referents; a model, when consulted, sees at most a small typed list and can never
/// introduce an entity that was not observed.
/// </summary>
public static class ReferentResolver
{
    public const int MaxCandidates = 8;

    /// <summary>
    /// Browser candidates from validated referents, most recent first. A live page supersedes the item that
    /// led to it (they denote the same thing); an item is offered only when no live page shows it.
    /// </summary>
    public static IReadOnlyList<ReferentCandidate> BuildCandidates(IReadOnlyList<Referent> valid, BrowserTabInfo? activeTab)
    {
        var pages = valid.Where(r => r.Kind == ReferentKind.Page).ToArray();
        var items = valid.Where(r => r.Kind == ReferentKind.Item
            && !pages.Any(p => SameAddress(p.Url, r.Href))).ToArray();
        var ordered = pages.Concat(items).OrderByDescending(static r => r.Seq).Take(MaxCandidates).ToArray();
        var groups = ordered.GroupBy(r => Host(r.Url ?? r.Href) ?? "").ToDictionary(g => g.Key, g => g.Count());
        return ordered.Select((r, i) =>
        {
            var host = Host(r.Url ?? r.Href) ?? "";
            var current = r.Kind == ReferentKind.Page ? activeTab is not null && r.TabId == activeTab.TabId
                : activeTab is not null && SameAddress(activeTab.Url, r.Href);
            return new ReferentCandidate($"ref_{i + 1}", r, current, groups[host] >= 2 ? host : null, i + 1);
        }).ToArray();
    }

    public static async ValueTask<ReferentResolution> ResolveAsync(string utterance,
        IReadOnlyList<ReferentCandidate> candidates, IContextualScopeDecisionSource? chooser,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) return ReferentResolution.Unavailable("no_candidates");
        // The one thing in view is the reference for an implicit "that": no model can improve on it.
        if (candidates is [{ Current: true } only]) return ReferentResolution.Selected(only, "unique_current");
        if (chooser is null) return ReferentResolution.Unavailable("no_referent_picker");
        var choice = await chooser.SelectReferentAsync(utterance, candidates, cancellationToken).ConfigureAwait(false);
        switch (choice.Kind)
        {
            case ReferentChoiceKind.None: return ReferentResolution.NoneFound("picker_none");
            case ReferentChoiceKind.Ambiguous:
                // "The other one" among an established pair is determined by the pair itself: when exactly one
                // member of a contrast group is not the thing in view, no further pick is needed.
                if (choice.Relation is ReferenceRelation.Alternative or ReferenceRelation.Previous
                    && candidates.Where(c => c.Group is not null).GroupBy(c => c.Group)
                        .SingleOrDefault(g => g.Count() == 2 && g.Count(c => c.Current) == 1)
                        is { } pair)
                    return ReferentResolution.Selected(pair.Single(c => !c.Current), "contrast_pair");
                return ReferentResolution.Ambiguous("picker_ambiguous");
            case ReferentChoiceKind.Unavailable: return ReferentResolution.Unavailable("picker_unavailable");
        }
        var picked = candidates.SingleOrDefault(c => c.Id == choice.CandidateId);
        if (picked is null) return ReferentResolution.Unavailable("candidate_not_offered");
        // "The other one" / "the previous one" only mean something against an established contrast set, and
        // never denote the thing already in view.
        if (choice.Relation is ReferenceRelation.Alternative or ReferenceRelation.Previous)
        {
            if (picked.Current) return ReferentResolution.Ambiguous("alternative_is_current");
            if (picked.Group is null) return ReferentResolution.Ambiguous("no_represented_contrast");
        }
        return ReferentResolution.Selected(picked, "picker");
    }

    private static string? Host(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static bool SameAddress(string? a, string? b)
        => Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
            && Uri.Compare(x, y, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}
