using System.Text.Json;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>One element as the observation evidence reports it (only what postconditions and shortcuts need).</summary>
internal sealed record EvidenceElement(string Id, string? Role, string? Name, string? Href, string? Value, bool Editable,
    bool Enabled, bool InViewport, string? Kind, string? SearchScope, string? SubmitRef, string? Form = null, bool Submit = false);

internal static class BrowserEvidence
{
    public static IReadOnlyList<EvidenceElement> Elements(string? evidence)
    {
        var list = new List<EvidenceElement>();
        try
        {
            if (evidence is null) return list;
            using var document = JsonDocument.Parse(evidence);
            if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var e in elements.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                if (Text("id") is not { } id) continue;
                list.Add(new(id, Text("Role"), Text("Name"), Text("Href"), Text("Value"), Flag("Editable"), Flag("Enabled"),
                    Flag("InViewport"), Text("Kind"), Text("SearchScope"), Text("SubmitRef"), Text("Form"), Flag("Submit")));
            }
        }
        catch { }
        return list;
    }

    public static string? VisibleText(string? evidence)
    {
        try
        {
            if (evidence is null) return null;
            using var document = JsonDocument.Parse(evidence);
            return document.RootElement.TryGetProperty("visible_text", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>More page lies below the viewport (scroll position + viewport height is short of the document height).</summary>
    public static bool CanScrollDown(string? evidence)
    {
        try
        {
            if (evidence is null) return false;
            using var document = JsonDocument.Parse(evidence);
            if (!document.RootElement.TryGetProperty("viewport", out var v)) return false;
            int Read(string name) => v.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            return Read("documentHeight") > 0 && Read("scrollY") + Read("height") + 40 < Read("documentHeight");
        }
        catch { return false; }
    }

    public static string? Url(string? evidence)
    {
        try
        {
            if (evidence is null) return null;
            using var document = JsonDocument.Parse(evidence);
            return document.RootElement.TryGetProperty("current_url", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// Deterministic, local postconditions for compiled plan steps. Each step asks one question of recorded
/// effects or of the fresh observation: never whether the whole request looks done, never a model. A wrongly
/// chosen target is a binding failure and is not corrected here.
/// </summary>
internal sealed class PlannedStepEvaluator(BrowserGoal goal, IProofEvaluator legacy) : IProofEvaluator
{
    private static readonly HashSet<string> GenericTargetWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "for", "and", "to", "in", "on", "page", "link", "result", "results", "section", "item",
        "repository", "repo", "button", "first", "second", "third", "open", "show", "find", "locate", "site", "website",
        "article", "video", "content", "one", "it", "that", "this", "its"
    };

    public ProofVerdict Evaluate(ProofInput input) => !input.Step.WithinPlan ? legacy.Evaluate(input) : input.Step.Family switch
    {
        ProofFamily.Surface => Reach(input),
        ProofFamily.Find => Search(input),
        ProofFamily.Locate => Locate(input),
        ProofFamily.Activate => Open(input),
        _ => ProofVerdict.NotYet(ProofFamily.Reach, "legacy_completion")
    };

    // -- Reach: the intended site is actually the current page -------------------------

    private ProofVerdict Reach(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Surface;
        var url = BrowserEvidence.Url(input.Observation.Evidence);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            return ProofVerdict.NotYet(family, "not_web_content");
        var destinations = BrowserCompletionEvidence.Destinations(goal).ToArray();
        if (destinations.Any(d => BrowserCompletionEvidence.IsSameSiteOrSubdomain(page.Host, d.Host)))
            return ProofVerdict.Proved(family, "reached_destination");
        // A site with no registered origin is reached when its own name is a label of the host.
        var compact = string.Concat(BrowserCompletionEvidence.Tokens(goal.Normalization?.PreferredService ?? input.Step.What.Phrase));
        return destinations.Length == 0 && compact.Length >= 3 && page.Host.ToLowerInvariant().Split('.').Contains(compact)
            ? ProofVerdict.Proved(family, "reached_named_site") : ProofVerdict.NotYet(family, "not_on_destination");
    }

    // -- Search: the query is in the field, the search was applied, and the page changed ---

    private ProofVerdict Search(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Find;
        if (input.LastAction is not { } action) return ProofVerdict.NotYet(family, "no_action");
        var textSet = input.Effects.LastOrDefault(static e => e.Kind == EffectKind.TextSet);
        if (textSet is null) return ProofVerdict.NotYet(family, "no_text_entered");
        var value = textSet.Get("value")?.Trim();
        var query = input.Step.What.Query?.Trim();
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.OrdinalIgnoreCase.Equals(value, query))
            return ProofVerdict.NotYet(family, "query_not_entered");
        var readback = textSet.Get("readback");
        // No readback means the field could not be re-identified after the action (the page changed under it); the
        // companion reported the entry succeeded. A readback that differs is a refusal of the text.
        if (textSet.Get("matched") != "true" && readback is not null
                && !string.Equals(Squash(readback), Squash(value), StringComparison.OrdinalIgnoreCase))
            return ProofVerdict.NotYet(family, "text_not_confirmed");
        // The consequence must come from an action after the one that entered the text: the submit.
        if (StringComparer.Ordinal.Equals(textSet.ActionId, action.Id)) return ProofVerdict.NotYet(family, "not_submitted");
        if (action.Kind is not (InteractionActionKind.Activate or InteractionActionKind.PressKey)) return ProofVerdict.NotYet(family, "not_submitted");
        var last = input.Effects.Where(e => StringComparer.Ordinal.Equals(e.ActionId, action.Id)).ToArray();
        if (last.Any(static e => e.Kind == EffectKind.NoEffect)) return ProofVerdict.NotYet(family, "no_effect");
        var navigated = last.FirstOrDefault(static e => e.Kind == EffectKind.Navigated);
        var changed = last.Any(static e => e.Kind == EffectKind.ContentChanged);
        if (navigated is null && !changed) return ProofVerdict.NotYet(family, "no_consequence");
        if (navigated?.Get("to") is { } landing && Uri.TryCreate(landing, UriKind.Absolute, out var page))
        {
            var destinations = BrowserCompletionEvidence.Destinations(goal).ToArray();
            if (destinations.Length > 0 && !destinations.Any(d => BrowserCompletionEvidence.IsSameSiteOrSubdomain(page.Host, d.Host)))
                return ProofVerdict.Inconclusive(family, "left_the_site");
        }
        return ProofVerdict.Proved(family, "search_applied", last.Append(textSet).Select(static e => e.Id).ToArray());
    }

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // -- Locate: the described target is now present in the content -------------------------

    private ProofVerdict Locate(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Locate;
        var terms = TargetTerms(input.Step.What.Phrase);
        // A target that names nothing distinctive ("the first result") has nothing to look for; binding decides later.
        if (terms.Length == 0) return ProofVerdict.Proved(family, "no_distinctive_target");
        return FindTarget(input.Observation.Evidence, terms) is { } found
            ? ProofVerdict.Proved(family, "target_present", detail: found) : ProofVerdict.NotYet(family, "target_not_present");
    }

    internal static string[] TargetTerms(string? target)
        => BrowserCompletionEvidence.Tokens(target).Where(t => !GenericTargetWords.Contains(t)).Select(Singular).Distinct().ToArray();

    /// <summary>The element (by ref) or "text" whose own words contain every distinctive term of the target.</summary>
    internal static string? FindTarget(string? evidence, string[] terms)
    {
        foreach (var element in BrowserEvidence.Elements(evidence).Where(static e => e.Enabled))
        {
            var words = BrowserCompletionEvidence.Tokens(element.Name + " " + PathWords(element.Href)).Select(Singular).ToHashSet();
            if (terms.All(words.Contains)) return element.Id;
        }
        var text = BrowserEvidence.VisibleText(evidence);
        if (text is null) return null;
        var shown = BrowserCompletionEvidence.Tokens(text).Select(Singular).ToHashSet();
        return terms.All(shown.Contains) ? "text" : null;
    }

    private static string PathWords(string? href)
        => Uri.TryCreate(href, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(uri.AbsolutePath) : "";

    private static string Singular(string term) => term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;

    // -- Open: the element VoiceOS deliberately bound was activated and the browser reports a consequence ---

    private static ProofVerdict Open(ProofInput input)
    {
        const ProofFamily family = ProofFamily.Activate;
        if (!input.Step.DescriptorReliable) return ProofVerdict.Inconclusive(family, "descriptor_unreliable");
        if (input.LastAction is not { Kind: InteractionActionKind.Activate } action)
            return ProofVerdict.NotYet(family, "no_activation");
        var last = input.Effects.Where(e => StringComparer.Ordinal.Equals(e.ActionId, action.Id)).ToArray();
        if (last.Any(static e => e.Kind == EffectKind.NoEffect)) return ProofVerdict.NotYet(family, "no_effect");
        var activated = last.FirstOrDefault(static e => e.Kind == EffectKind.Activated);
        if (activated?.Subject is not { ElementRef: not null } subject) return ProofVerdict.NotYet(family, "no_activation_effect");
        // A means action (opening a search dialog, pagination) carries no binding: it is not the step's target.
        if (input.TargetBinding is not { } binding) return ProofVerdict.NotYet(family, "no_binding");
        if (!StringComparer.Ordinal.Equals(binding.ElementRef, subject.ElementRef)
            || binding.ObservationRevision != subject.ObservationRevision)
            return ProofVerdict.Inconclusive(family, "target_mismatch");
        var consequence = last.Any(static e => e.Kind is EffectKind.Navigated or EffectKind.ContentChanged
            || e.Kind == EffectKind.SurfaceAcquired && e.Get("mode") == "adopted");
        return consequence
            ? ProofVerdict.Proved(family, "bound_activation_with_effect", last.Select(static e => e.Id).ToArray())
            : ProofVerdict.NotYet(family, "no_consequence");
    }
}
