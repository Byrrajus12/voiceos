using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>The outcome of finding a chosen option again on the page as it is now.</summary>
internal enum RebindKind { Found, Ambiguous, Missing }

internal sealed record RebindResult(RebindKind Kind, string? ElementId = null, string Via = "");

/// <summary>
/// Finds the option the user picked among the controls on the page now, by semantic identity and never by DOM reference. The
/// identity hierarchy, strongest first: (1) the canonical address; (2) the same label (its own or another handle's of the same result)
/// with the same role and container text; (3) a bounded multi-feature match (label overlap plus container or list position).
/// Each step must single out exactly one semantic result; several is refused, never guessed.
/// </summary>
internal static class ChoiceRebinder
{
    private static string Norm(string? text) => string.Join(' ', BrowserCompletionEvidence.Tokens(text));
    private static string Name(EvidenceElement e) => !string.IsNullOrWhiteSpace(e.Name) ? e.Name! : e.Value ?? "";

    public static RebindResult Find(ChoiceOption option, IReadOnlyList<EvidenceElement> elements, Func<string, bool> offered)
    {
        var live = elements.Where(e => e.Enabled && offered(e.Id)).ToArray();
        var labels = new[] { option.DisplayText }.Concat(option.AlternateLabels ?? []).Select(Norm).Where(static n => n.Length > 0).ToHashSet();
        var wantedHref = TargetAmbiguity.CanonicalHref(option.Href);
        var context = Norm(option.Context);
        bool RoleOk(EvidenceElement e) => option.Role is null || string.Equals(e.Role, option.Role, StringComparison.OrdinalIgnoreCase);

        // 1. Where it leads: every handle of one canonical address is one result.
        if (wantedHref is not null)
        {
            var byAddress = live.Where(e => TargetAmbiguity.CanonicalHref(e.Href) == wantedHref).ToArray();
            if (byAddress.Length > 0) return Pick(byAddress, labels, "address");
        }

        // 2. The same words in the same kind of control under the same container text. With no container text to corroborate it,
        // the words alone are not identity once the address has gone (another result may share them).
        var named = live.Where(e => labels.Contains(Norm(Name(e))) && RoleOk(e)).ToArray();
        if (named.Length > 0 && (context.Length > 0 || wantedHref is null))
        {
            var strong = context.Length > 0 ? named.Where(e => Norm(e.Context) == context).ToArray() : named;
            if (strong.Length > 1 && option.Position is not null)
                strong = strong.Where(e => e.Position == option.Position).ToArray() is { Length: > 0 } byPosition ? byPosition : strong;
            if (strong.Length > 0) return Pick(strong, labels, "label_container");
        }

        // 3. Bounded multi-feature: most of the label's words, plus the container text or the list position.
        var own = Norm(option.DisplayText).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (own.Count == 0) return new(RebindKind.Missing);
        var scored = live.Where(RoleOk).Select(e =>
            {
                var words = Norm(Name(e)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                var overlap = words.Count == 0 ? 0 : (double)words.Intersect(own).Count() / words.Union(own).Count();
                var corroborated = context.Length > 0 && Norm(e.Context) == context || option.Position is not null && e.Position == option.Position;
                return (Element: e, Overlap: overlap, Corroborated: corroborated);
            })
            .Where(static x => x.Overlap >= .7 && x.Corroborated).ToArray();
        return scored.Length > 0 ? Pick(scored.Select(static x => x.Element).ToArray(), labels, "multi_feature") : new(RebindKind.Missing);
    }

    /// <summary>One semantic result must remain (handles of it collapse); then its best handle is chosen.</summary>
    private static RebindResult Pick(EvidenceElement[] candidates, HashSet<string> labels, string via)
    {
        var groups = candidates.GroupBy(TargetAmbiguity.GroupKey).ToArray();
        if (groups.Length != 1) return new(RebindKind.Ambiguous, Via: via);
        var best = groups[0].OrderByDescending(e => labels.Contains(Norm(Name(e)))).First();
        return new(RebindKind.Found, best.Id, via);
    }
}
