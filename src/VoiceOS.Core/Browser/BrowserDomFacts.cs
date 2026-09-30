using System.Text.RegularExpressions;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Structural facts derived in code from what the companion reports (role, name, context, href, search/form/landmark
/// markers). They spare the decision model from re-inferring obvious page structure from raw strings. Nothing here
/// knows any site: only ARIA/form/landmark semantics and generic wording of the control's own label.
/// </summary>
public sealed record ElementFacts(string Ref, string Kind, string? SearchScope = null, string? SubmitRef = null,
    string? Landmark = null, bool InList = false, bool? Selected = null);

public static class BrowserDomFacts
{
    public const string SearchField = "search_field";
    public const string SubmitControl = "submit";
    public const string NavigationLink = "nav_link";
    public const string ResultItem = "result_item";
    public const string Field = "field";
    public const string Control = "control";

    // "Search this repository", "Search in this project", "Search within page": the control says it is narrower than the site.
    private static readonly Regex LocalScope = new(
        @"\b(within|in\s+this|this\s+(?:repo(?:sitory)?|page|project|org(?:anization)?|folder|channel|conversation|thread|collection|category|section|document|file|list|playlist|channel|profile))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SiteScope = new(@"\b(this\s+(?:site|website)|all\s+of|entire\s+site|everywhere|the\s+web)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A text field labelled as a search box (a combobox outside any [role=search] landmark) is still a search field.
    private static readonly Regex NamedSearch = new(@"search", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubmitWords = new(@"^\s*(search|go|find|submit|apply|enter)\b|\bsearch\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, ElementFacts> Derive(IReadOnlyList<BrowserElement> elements)
    {
        var submits = elements.Where(static e => e.Enabled && (e.Role is "button" or "link") && IsSubmit(e)).ToArray();
        var result = new Dictionary<string, ElementFacts>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var searchField = element.Editable && (element.Search || element.Role == "searchbox" || NamedSearch.IsMatch(element.Name ?? ""));
            var kind = searchField ? SearchField
                : element.Editable ? Field
                : element.Submit ? SubmitControl
                : element.Role == "link" && element.Landmark == "navigation" ? NavigationLink
                : element.Role == "link" && element.InList ? ResultItem
                : Control;
            string? scope = searchField ? SearchScopeOf(element) : null;
            string? submitRef = null;
            if (searchField && !string.IsNullOrEmpty(element.Form))
                submitRef = submits.Where(s => s.Form == element.Form).OrderByDescending(static s => s.Submit).FirstOrDefault()?.Ref;
            result[element.Ref] = new(element.Ref, kind, scope, submitRef, element.Landmark, element.InList, element.Selected);
        }
        return result;
    }

    /// <summary>"site" when the field searches the whole site, "local" when its own wording or form action narrows it.</summary>
    public static string SearchScopeOf(BrowserElement element)
    {
        var words = $"{element.Name} {element.Value}".Trim();
        if (SiteScope.IsMatch(words)) return "site";
        if (LocalScope.IsMatch(words)) return "local";
        // A form that posts to a deep path (/owner/repo/search) searches inside that path, not the site root.
        if (element.FormAction is { Length: > 0 } action)
        {
            var segments = action.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 || segments.Length == 2 && !segments[^1].Equals("search", StringComparison.OrdinalIgnoreCase))
                return "local";
        }
        return "site";
    }

    private static bool IsSubmit(BrowserElement e) => e.Submit || SubmitWords.IsMatch(e.Name ?? "");
}
