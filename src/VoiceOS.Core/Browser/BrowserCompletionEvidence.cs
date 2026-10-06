using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Unestablished: a specific sub-resource was requested and the page is only the site's default
/// landing, with nothing yet showing the resource. This is NOT proof the resource is absent (an
/// SPA can render a menu at the root URL); it only means completion needs positive evidence.
/// Confirmed: the observation itself is the typed end state, so no model judgment is needed.
/// </summary>
public enum CompletionEvidenceStrength { Unestablished, Neutral, Supporting, Strong, Confirmed }

/// <summary>What one browser observation, plus the in-page actions that led to it, shows.</summary>
public sealed record BrowserPageFacts(string? Url, string? Title, IReadOnlyList<string>? OpenedControls = null)
{
    /// <summary>
    /// Reads the observation evidence and collects the names of controls that were activated on
    /// this site, visibly changed the page, and were not followed by a navigation away: the
    /// in-page state they opened is the state now observed.
    /// </summary>
    public static BrowserPageFacts From(InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> history)
    {
        var (url, title) = Read(observation.Evidence);
        var opened = new List<string>();
        var host = Host(url);
        for (var index = history.Count - 1; index >= 0 && host is not null; index--)
        {
            var entry = history[index];
            var before = Read(entry.ObservationEvidence);
            if (!StringComparer.Ordinal.Equals(Read(entry.ResultingEvidence).Url, url)) break;
            if (entry.Action.Kind == InteractionActionKind.Activate && entry.Result.Succeeded
                && entry.ObservationStateKey != entry.ResultingStateKey
                && StringComparer.OrdinalIgnoreCase.Equals(Host(before.Url), host)
                && ElementName(entry.ObservationEvidence, entry.Action.TargetId) is { } name)
                opened.Add(name);
        }
        return new(url, title, opened);
    }

    private static string? Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static (string? Url, string? Title) Read(string? evidence)
    {
        try
        {
            if (evidence is null) return (null, null);
            using var document = JsonDocument.Parse(evidence);
            var root = document.RootElement;
            return (root.TryGetProperty("current_url", out var url) ? url.GetString() : null,
                root.TryGetProperty("current_title", out var title) ? title.GetString() : null);
        }
        catch { return (null, null); }
    }

    private static string? ElementName(string? evidence, string? id)
    {
        try
        {
            if (evidence is null || id is null) return null;
            using var document = JsonDocument.Parse(evidence);
            if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var element in elements.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var ref_)
                    && ref_.GetString() == id && element.TryGetProperty("Name", out var name))
                    return name.GetString();
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Deterministic evidence about whether a page represents the normalized end state. Positive
/// observations lower how much independent model agreement (goal_achieved) acceptance needs,
/// or, where the observation is the end state itself, replace it. Missing URL/title evidence
/// is unknown, never proof of absence.
/// </summary>
public sealed partial record BrowserCompletionEvidence(CompletionEvidenceStrength Strength, string Reason)
{
    public const double StrongThreshold = .40;
    public const double SupportingThreshold = .45;

    public bool IsUnestablished => Strength == CompletionEvidenceStrength.Unestablished;
    public bool IsConfirmed => Strength == CompletionEvidenceStrength.Confirmed;

    /// <summary>The goal_achieved probability required for acceptance; never above the neutral threshold.</summary>
    public double Threshold(double neutralThreshold) => Strength switch
    {
        CompletionEvidenceStrength.Confirmed => 0,
        CompletionEvidenceStrength.Strong => Math.Min(StrongThreshold, neutralThreshold),
        CompletionEvidenceStrength.Supporting => Math.Min(SupportingThreshold, neutralThreshold),
        CompletionEvidenceStrength.Neutral => neutralThreshold,
        _ => double.PositiveInfinity
    };

    public static BrowserCompletionEvidence Evaluate(BrowserGoal goal, string? url, string? title)
        => Evaluate(goal, new BrowserPageFacts(url, title));

    public static BrowserCompletionEvidence Evaluate(BrowserGoal goal, BrowserPageFacts facts)
    {
        if (!Uri.TryCreate(facts.Url, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https"))
            return Neutral("no_web_page");
        if (goal.Normalization is not { } normalized)
            return Neutral("no_typed_goal");

        var destinations = Destinations(goal).ToArray();
        var onDestination = destinations.Any(destination => IsSameSiteOrSubdomain(page.Host, destination.Host));
        var namedHost = NameIsHost(normalized.Entity, page.Host) || NameIsHost(normalized.PreferredService, page.Host);
        var atRoot = page.AbsolutePath is "/" or "" && page.Query.Length == 0;
        var siteItself = string.IsNullOrWhiteSpace(normalized.ResourceType) || IsSiteItself(normalized.ResourceType);
        var resourceTerms = DistinctiveResourceTerms(normalized, destinations);
        // An in-page control named for the requested resource was opened and the page still shows
        // the state it produced (e.g. "Open menu categories" on an SPA whose URL never changes).
        var resourceControlOpened = (onDestination || namedHost) && resourceTerms.Length > 0
            && (facts.OpenedControls ?? []).Any(name => Tokens(name).Select(Singular).Any(resourceTerms.Contains));

        switch (normalized.EndState)
        {
            case SemanticEndState.SurfaceReady:
                // SurfaceReady defines arrival at the requested site as the whole end state. A
                // specific sub-resource type keeps it model-judged (it is not the site itself).
                if (onDestination)
                    return siteItself ? Confirmed("requested_site_reached") : Strong("requested_origin");
                return namedHost ? Supporting("named_host") : Neutral("surface_not_confirmed");

            case SemanticEndState.ResultsVisible:
                if (QueryShown(normalized, destinations, page) && (onDestination || destinations.Length == 0))
                    return Strong("query_results_on_requested_site");
                return (onDestination || namedHost) && !atRoot
                    ? Supporting("on_requested_site") : Neutral("results_not_confirmed");

            case SemanticEndState.ResourceOpened or SemanticEndState.ResourceLocated:
                if (atRoot && IsSiteItself(normalized.ResourceType))
                    return onDestination || namedHost ? Supporting("site_home") : Neutral("site_not_confirmed");
                if (!atRoot)
                {
                    var pageText = $"{facts.Title} {Uri.UnescapeDataString(page.AbsolutePath)}";
                    // An explicit sub-resource (menu, baggage policy) must itself show; the entity alone
                    // only proves the site (shakeshack.com/locations is Shake Shack, not its menu).
                    var resourceShown = resourceTerms.Length == 0 || Tokens(pageText).Select(Singular).Any(resourceTerms.Contains);
                    var entityShown = EntityShown(normalized.Entity, pageText) && resourceShown;
                    if (onDestination && entityShown) return Strong("entity_on_requested_site");
                    if (namedHost && entityShown) return Supporting("entity_on_named_host");
                }
                if (resourceControlOpened)
                    return Supporting("resource_control_opened");
                return atRoot && !siteItself
                    ? new(CompletionEvidenceStrength.Unestablished, "sub_resource_not_yet_shown")
                    : Neutral("resource_not_confirmed");

            default:
                // ContentActive, StateChanged, OtherBoundedGoal: whether the requested content was
                // reached/activated is the semantic model's judgment; playback is not verified.
                return Neutral("state_goal_model_judged");
        }
    }

    private static BrowserCompletionEvidence Confirmed(string reason) => new(CompletionEvidenceStrength.Confirmed, reason);
    private static BrowserCompletionEvidence Strong(string reason) => new(CompletionEvidenceStrength.Strong, reason);
    private static BrowserCompletionEvidence Supporting(string reason) => new(CompletionEvidenceStrength.Supporting, reason);
    private static BrowserCompletionEvidence Neutral(string reason) => new(CompletionEvidenceStrength.Neutral, reason);

    internal static IEnumerable<Uri> Destinations(BrowserGoal goal)
    {
        if (goal.ExplicitUrl is { } explicitUrl) yield return explicitUrl;
        if (goal.ScopedDestination is { } scoped) yield return scoped;
        if (ServiceResolver.Resolve(goal.NamedServiceHint)?.WebOrigin is { } named) yield return named;
        if (ServiceResolver.Resolve(goal.Normalization?.PreferredService)?.WebOrigin is { } service) yield return service;
        if (Uri.TryCreate(goal.Normalization?.PreferredServiceUrl, UriKind.Absolute, out var preferred)) yield return preferred;
    }

    /// <summary>Directional: a subdomain of the destination counts, a parent domain does not
    /// (google.com is not Gmail at mail.google.com).</summary>
    internal static bool IsSameSiteOrSubdomain(string pageHost, string destinationHost)
    {
        var page = StripWww(pageHost);
        var destination = StripWww(destinationHost);
        return destination.Length > 0 && (page == destination || page.EndsWith("." + destination, StringComparison.Ordinal));
    }

    private static string StripWww(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>The whole name is one host label (Stack Overflow → stackoverflow.com, NVIDIA → nvidia.wd5.…).</summary>
    private static bool NameIsHost(string? name, string host)
    {
        var compact = string.Concat(Tokens(name));
        return compact.Length >= 3 && host.ToLowerInvariant().Split('.').Contains(compact);
    }

    /// <summary>All terms of the entity or of one search query (minus the destination's own name)
    /// appear in the page's query string or path, i.e. the page shows results for that query.</summary>
    private static bool QueryShown(BrowserGoalNormalization goal, IReadOnlyList<Uri> destinations, Uri page)
    {
        var siteWords = new HashSet<string>(Tokens(goal.PreferredService).Concat(destinations
            .SelectMany(destination => StripWww(destination.Host).Split('.'))));
        var shown = Tokens(Uri.UnescapeDataString(page.Query.Replace('+', ' ')) + " "
            + Uri.UnescapeDataString(page.AbsolutePath)).ToHashSet();
        return new[] { goal.Entity }.Concat(goal.SearchQueries).Any(text =>
        {
            var terms = Tokens(text).Where(term => !siteWords.Contains(term)).ToArray();
            return terms.Length > 0 && terms.All(shown.Contains);
        });
    }

    /// <summary>A strict majority of the entity's terms appear in the text, so
    /// "Dune (2021 film)" matches "Dune: Part One (2021)" but not "Dune: Part Two (2024)".</summary>
    private static bool EntityShown(string? entity, string text)
    {
        var terms = Tokens(entity).Distinct().ToArray();
        if (terms.Length == 0) return false;
        var shown = Tokens(text).ToHashSet();
        return terms.Count(shown.Contains) * 2 > terms.Length;
    }

    /// <summary>The resource type's distinctive terms: not generic words, the entity, or the
    /// site's own name ("checked baggage policy page" → checked, baggage, policy).</summary>
    private static string[] DistinctiveResourceTerms(BrowserGoalNormalization goal, IReadOnlyList<Uri> destinations)
    {
        if (IsSiteItself(goal.ResourceType)) return [];
        var excluded = new HashSet<string>(GenericResourceWords.Concat(Tokens(goal.PreferredService))
            .Concat(Tokens(goal.Entity)).Concat(destinations.SelectMany(d => StripWww(d.Host).Split('.')))
            .Select(Singular));
        return Tokens(goal.ResourceType).Select(Singular).Where(term => !excluded.Contains(term)).Distinct().ToArray();
    }

    private static readonly string[] GenericResourceWords = ["page", "official", "main", "the", "a", "an", "of", "for", "and", "or", "on"];

    private static string Singular(string term) => term.Length > 3 && term.EndsWith('s') ? term[..^1] : term;

    /// <summary>The requested resource is the site itself (website, homepage), not a page within it.</summary>
    internal static bool IsSiteItself(string? resourceType)
        => resourceType is not null && SiteWords().IsMatch(resourceType);

    internal static IEnumerable<string> Tokens(string? text) => text is null ? []
        : WordPattern().Matches(text.ToLowerInvariant()).Select(match => match.Value);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\b(web\s*site|site|home\s*page|front\s*page|portal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SiteWords();
}
