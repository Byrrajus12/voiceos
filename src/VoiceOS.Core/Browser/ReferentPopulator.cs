using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Pure translation of what one browser run actually did (its effect ledger and final surface) into
/// referents. Only observed facts qualify: no model output, no candidate that was merely offered, no
/// action whose consequence was not seen.
/// </summary>
public static class ReferentPopulator
{
    public static IReadOnlyList<Referent> FromBrowserRun(IReadOnlyList<Effect>? effects,
        InteractionCompletionState completion, int? tabId, string? sessionId, string? url, string? title,
        DateTimeOffset now)
    {
        // A failed run establishes nothing: its page may be a dead end the user never asked for.
        if (completion == InteractionCompletionState.Incomplete
            || tabId is not int tab || sessionId is null || string.IsNullOrEmpty(url)) return [];
        effects ??= [];
        var referents = new List<Referent>();

        var acquired = effects.FirstOrDefault(e => e.Kind == EffectKind.SurfaceAcquired && e.Subject?.TabId == tab);
        var navigated = effects.Any(e => e.Kind is EffectKind.Navigated or EffectKind.HistoryMoved);
        var provenance = navigated ? ReferentProvenance.NavigationResult
            : acquired is not null ? ReferentProvenance.SurfaceAcquired : ReferentProvenance.ObservedActivePage;
        var label = string.IsNullOrWhiteSpace(title) ? url : title!;

        if (acquired is not null)
            referents.Add(new(ReferentKind.BrowserTab, label, ReferentProvenance.SurfaceAcquired, now,
                tab, sessionId, url, OwnedByVoiceOs: true));
        referents.Add(new(ReferentKind.Page, label, provenance, now, tab, sessionId, url,
            OwnedByVoiceOs: acquired is not null));

        if (completion == InteractionCompletionState.Complete && FinalItem(effects, tab, url, now, sessionId) is { } item)
            referents.Add(item);
        return referents;
    }

    /// <summary>
    /// The item is the run's last activation, and only when its own consequence was observed and is where the run
    /// ended. Earlier confirmed activations were only the means (a search box, a results link).
    /// </summary>
    private static Referent? FinalItem(IReadOnlyList<Effect> effects, int tabId, string finalUrl, DateTimeOffset now,
        string sessionId)
    {
        var index = -1;
        for (var i = effects.Count - 1; i >= 0; i--)
            if (effects[i].Kind == EffectKind.Activated && effects[i].Subject is not null) { index = i; break; }
        if (index < 0) return null;
        var activated = effects[index];
        var subject = activated.Subject!;
        if (string.IsNullOrWhiteSpace(subject.Label)) return null;
        var after = effects.Skip(index + 1).ToArray();

        var landed = after.FirstOrDefault(e => e.Kind == EffectKind.Navigated && e.ActionId is not null
            && e.ActionId == activated.ActionId);
        if (landed is not null)
        {
            var to = landed.Get("to");
            // Something later moved the tab again: the activation is not where the run ended.
            if (to is null || !StringComparer.Ordinal.Equals(to, finalUrl)
                || after.Any(e => e.Kind is EffectKind.Navigated or EffectKind.HistoryMoved && !ReferenceEquals(e, landed)))
                return null;
            return Make(subject, to, landed.Get("from"), tabId, sessionId, now);
        }
        // Continued into a child tab the surface adopted; the child page is where the run ended.
        var adopted = after.FirstOrDefault(e => e.Kind == EffectKind.SurfaceAcquired && e.Get("mode") == "adopted"
            && e.Get("fromTabId") == subject.TabId.ToString() && e.Subject?.TabId == tabId);
        return adopted is null ? null : Make(subject, finalUrl, null, tabId, sessionId, now);
    }

    private static Referent? Make(TypedRef subject, string landing, string? from, int tabId, string sessionId, DateTimeOffset now)
    {
        // The link's own address is the identity when it has one; otherwise the observed landing.
        var href = Uri.TryCreate(subject.Href, UriKind.Absolute, out var link) && link.Scheme is "http" or "https"
            ? link.AbsoluteUri : landing;
        return new(ReferentKind.Item, subject.Label, ReferentProvenance.ActivatedTarget, now, tabId, sessionId,
            Href: href, Fingerprint: subject.Fingerprint, SourceUrl: from);
    }
}
