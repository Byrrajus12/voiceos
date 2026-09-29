using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Decides from recorded effects whether a framed step's outcome is established. Pure: no I/O, no model.
/// It answers "did VoiceOS carry out what it chose, with a consequence attributable to it", never
/// "did VoiceOS understand the user"; a doubtful descriptor or binding therefore caps the verdict.
/// </summary>
internal sealed class BrowserProofEvaluator(BrowserGoal goal, ProofThresholds? thresholds = null) : IProofEvaluator
{
    private static readonly string[] QueryParameters =
        ["q", "query", "search", "search_query", "text", "keyword", "keywords", "term", "k", "s"];

    private readonly ProofThresholds _thresholds = thresholds ?? new();

    public ProofVerdict Evaluate(ProofInput input) => input.Step.Family switch
    {
        ProofFamily.Surface => Surface(input),
        ProofFamily.Find => Find(input),
        ProofFamily.Activate => Activate(input),
        _ => ProofVerdict.NotYet(ProofFamily.Reach, "legacy_completion")
    };

    // -- Surface ---------------------------------------------------------------

    private ProofVerdict Surface(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Surface;
        // Acquisition is a start-of-run fact; once an action has run the page may have moved on.
        if (input.LastAction is not null) return ProofVerdict.NotYet(family, "surface_initial_only");
        var acquired = input.Effects.Where(static e => e.Kind == EffectKind.SurfaceAcquired).ToArray();
        // An already-active tab that VoiceOS did not open or adopt is not an acquired surface.
        if (acquired.Length == 0) return ProofVerdict.NotYet(family, "no_acquisition");
        var url = CurrentUrl(input.Observation);
        if (!IsWeb(url, out var page)) return ProofVerdict.Inconclusive(family, "not_web_content");
        var destinations = BrowserCompletionEvidence.Destinations(goal).ToArray();
        if (destinations.Length == 0) return ProofVerdict.Inconclusive(family, "no_known_destination");
        return destinations.Any(d => BrowserCompletionEvidence.IsSameSiteOrSubdomain(page.Host, d.Host))
            ? ProofVerdict.Proved(family, "surface_acquired_on_destination", Ids(acquired))
            : ProofVerdict.Inconclusive(family, "wrong_origin");
    }

    // -- Find ------------------------------------------------------------------

    private ProofVerdict Find(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Find;
        if (!input.Step.DescriptorReliable) return ProofVerdict.Inconclusive(family, "descriptor_unreliable");
        if (input.LastAction is not { } action) return ProofVerdict.NotYet(family, "no_action");
        var textSet = input.Effects.LastOrDefault(static e => e.Kind == EffectKind.TextSet);
        if (textSet is null) return ProofVerdict.NotYet(family, "no_text_entered");
        var value = textSet.Get("value")?.Trim();
        if (!Grounded(value, input.Step)) return ProofVerdict.Inconclusive(family, "query_not_grounded");
        if (textSet.Get("matched") != "true") return ProofVerdict.Inconclusive(family, "text_not_confirmed");
        // The consequence must come from a later action than the one that entered the text.
        if (StringComparer.Ordinal.Equals(textSet.ActionId, action.Id)) return ProofVerdict.NotYet(family, "not_submitted");
        var last = ForAction(input.Effects, action.Id);
        if (last.Any(static e => e.Kind == EffectKind.NoEffect)) return ProofVerdict.NotYet(family, "no_effect");
        var navigated = last.FirstOrDefault(static e => e.Kind == EffectKind.Navigated);
        if (navigated is null)
            return last.Any(static e => e.Kind is EffectKind.ContentChanged or EffectKind.SurfaceAcquired)
                ? ProofVerdict.Inconclusive(family, "no_navigation_observed", effects: Ids(last))
                : ProofVerdict.NotYet(family, "no_consequence");
        var landing = navigated.Get("to");
        if (!IsWeb(landing, out var page)) return ProofVerdict.Inconclusive(family, "not_web_content", effects: Ids(last));
        var destinations = BrowserCompletionEvidence.Destinations(goal).ToArray();
        if (destinations.Length > 0 && !destinations.Any(d => BrowserCompletionEvidence.IsSameSiteOrSubdomain(page.Host, d.Host)))
            return ProofVerdict.Inconclusive(family, "wrong_origin", effects: Ids(last));

        var siteWords = SiteWords(destinations);
        var terms = BrowserCompletionEvidence.Tokens(value).Where(t => !siteWords.Contains(t)).Distinct().ToArray();
        if (terms.Length == 0) return ProofVerdict.Inconclusive(family, "query_only_site_words");
        var shown = BrowserCompletionEvidence.Tokens(Uri.UnescapeDataString(page.Query.Replace('+', ' '))
            + " " + Uri.UnescapeDataString(page.AbsolutePath)).ToHashSet();
        if (terms.All(shown.Contains))
            return ProofVerdict.Proved(family, "query_results_shown", Ids(last.Append(textSet)));
        // A page whose URL is opaque may still title the query; a URL that names a different query is a contradiction.
        var titled = BrowserCompletionEvidence.Tokens(CurrentTitle(input.Observation)).ToHashSet();
        if (terms.All(titled.Contains))
            return ProofVerdict.Proved(family, "query_results_titled", Ids(last.Append(textSet)));
        return ExplicitQueryContradicts(page, terms, siteWords)
            ? ProofVerdict.Refuted(family, "query_mismatch", effects: Ids(last))
            : ProofVerdict.Inconclusive(family, "query_not_shown", effects: Ids(last));
    }

    private static bool Grounded(string? value, OutcomeStep step)
        => !string.IsNullOrWhiteSpace(value)
            && step.What.Query is { } query && StringComparer.OrdinalIgnoreCase.Equals(query.Trim(), value);

    /// <summary>The URL names an explicit query whose terms share nothing with what was typed.</summary>
    private static bool ExplicitQueryContradicts(Uri page, string[] typedTerms, HashSet<string> siteWords)
    {
        foreach (var pair in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || !QueryParameters.Contains(pair[..eq], StringComparer.OrdinalIgnoreCase)) continue;
            var carried = BrowserCompletionEvidence.Tokens(Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')))
                .Where(t => !siteWords.Contains(t)).ToArray();
            if (carried.Length > 0 && !carried.Any(typedTerms.Contains)) return true;
        }
        return false;
    }

    // -- Activate --------------------------------------------------------------

    private ProofVerdict Activate(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Activate;
        if (!input.Step.DescriptorReliable) return ProofVerdict.Inconclusive(family, "descriptor_unreliable");
        if (input.LastAction is not { Kind: InteractionActionKind.Activate } action)
            return ProofVerdict.NotYet(family, "no_activation");
        var last = ForAction(input.Effects, action.Id);
        if (last.Any(static e => e.Kind == EffectKind.NoEffect)) return ProofVerdict.NotYet(family, "no_effect");
        var activated = last.FirstOrDefault(static e => e.Kind == EffectKind.Activated);
        if (activated?.Subject is not { ElementRef: not null } subject)
            return ProofVerdict.NotYet(family, "no_activation_effect");
        // Means actions (search box submit, menu, pagination) carry no binding and therefore no proof.
        if (input.TargetBinding is not { } binding) return ProofVerdict.NotYet(family, "no_binding");
        var strength = _thresholds.Classify(binding);
        if (strength == BindingStrength.Weak) return ProofVerdict.Inconclusive(family, "weak_binding", Describe(binding));
        if (!StringComparer.Ordinal.Equals(binding.ElementRef, subject.ElementRef)
            || binding.ObservationRevision != subject.ObservationRevision)
            return ProofVerdict.Inconclusive(family, "target_mismatch", Describe(binding));

        var navigated = last.FirstOrDefault(static e => e.Kind == EffectKind.Navigated);
        var adopted = last.FirstOrDefault(static e => e.Kind == EffectKind.SurfaceAcquired && e.Get("mode") == "adopted");
        var changed = last.FirstOrDefault(static e => e.Kind == EffectKind.ContentChanged);
        var landing = adopted?.Get("url") ?? navigated?.Get("to");
        var evidence = Ids(last);

        if (!string.IsNullOrWhiteSpace(subject.Href))
        {
            if (landing is null)
                return ProofVerdict.Inconclusive(family, changed is not null ? "link_without_navigation" : "no_consequence",
                    Describe(binding), evidence);
            var relation = UrlConsistency.Relate(subject.Href, landing);
            var detail = $"{Describe(binding)} relation={relation}";
            // A landing that differs from the link's own URL is never a contradiction by itself: shorteners,
            // tracking hops and SSO are indistinguishable from a wrong page by host alone (live shadow data refuted
            // a legitimate short link). It simply does not prove; the legacy path decides.
            if (relation == UrlRelation.Inconsistent)
                return ProofVerdict.Inconclusive(family, "inconsistent_destination", detail, evidence);
            if (relation == UrlRelation.SameOrigin && !_thresholds.AllowSameOriginLink)
                return ProofVerdict.Inconclusive(family, "same_origin_only", detail, evidence);
            if (strength == BindingStrength.Moderate
                && !Corroborated(landing, adopted is null ? CurrentTitle(input.Observation) : null, input.Step))
                return ProofVerdict.Inconclusive(family, "moderate_binding_uncorroborated", detail, evidence);
            return ProofVerdict.Proved(family, $"link_followed_{relation.ToString().ToLowerInvariant()}", evidence, $"{detail} strength={strength}");
        }

        // A control without a destination cannot prove an opened/used target in this pass. A page change after
        // a bare button click is indistinguishable from a means action (search submit, filter chip, menu), and a
        // strongly bound but wrong control produced exactly that in live shadow data (a repeatedly clicked
        // button that changed the page without reaching the goal). The legacy path decides these.
        if (changed is not null || navigated is not null || adopted is not null)
            return ProofVerdict.Inconclusive(family, "non_navigating_control", Describe(binding), evidence);
        return ProofVerdict.NotYet(family, "no_consequence");
    }

    private static bool Corroborated(string landing, string? title, OutcomeStep step)
    {
        if (!Uri.TryCreate(landing, UriKind.Absolute, out var uri)) return false;
        var terms = BrowserCompletionEvidence.Tokens(step.What.Phrase).Where(static t => t.Length >= 4)
            .Where(static t => !GenericWords.Contains(t)).Distinct().ToArray();
        var shown = BrowserCompletionEvidence.Tokens(Uri.UnescapeDataString(uri.AbsolutePath) + " " + uri.Host + " " + title).ToHashSet();
        return terms.Any(shown.Contains);
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
        { "open", "click", "link", "page", "result", "results", "this", "that", "with", "from", "video", "third", "second", "first" };

    // -- helpers ---------------------------------------------------------------

    private static string Describe(TargetBinding binding)
        => $"method={binding.Method} p={binding.P?.ToString("F2") ?? "-"} margin={binding.Margin?.ToString("F2") ?? "-"} candidates={binding.CandidateCount}";

    private static Effect[] ForAction(IReadOnlyList<Effect> effects, string actionId)
        => effects.Where(e => StringComparer.Ordinal.Equals(e.ActionId, actionId)).ToArray();

    private static string[] Ids(IEnumerable<Effect> effects) => effects.Select(static e => e.Id).ToArray();

    private HashSet<string> SiteWords(IEnumerable<Uri> destinations)
        => new(destinations.SelectMany(d => d.Host.ToLowerInvariant().Split('.')).Concat(
            BrowserCompletionEvidence.Tokens(goal.Normalization?.PreferredService)));

    private static string? CurrentUrl(InteractionObservation observation) => BrowserPageFacts.From(observation, []).Url;
    private static string? CurrentTitle(InteractionObservation observation) => BrowserPageFacts.From(observation, []).Title;

    private static bool IsWeb(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https") return true;
        uri = null!;
        return false;
    }
}
