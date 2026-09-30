using System.Text.RegularExpressions;

namespace VoiceOS.Core.Browser;

/// <summary>
/// What a search surface searches. Derived only from the control's own wording, form action and ARIA/landmark
/// structure, never from a site. Global is the broad service-wide search (including a control that opens one);
/// Collection is a bounded set ("Find a repository" among the user's own); CurrentResource searches inside the
/// open repository/document/project; InPage finds text on the rendered page; Filter narrows what is already shown.
/// </summary>
public static class SearchScopes
{
    public const string Global = "Global";
    public const string Collection = "Collection";
    public const string CurrentResource = "CurrentResource";
    public const string InPage = "InPage";
    public const string Filter = "Filter";
    public const string Unknown = "Unknown";

    /// <summary>True when the scope is known and is not the wanted one (Unknown never blocks).</summary>
    public static bool Conflicts(string? actual, string wanted)
        => actual is Global or Collection or CurrentResource or InPage or Filter && actual != wanted;
}

/// <summary>Structural facts derived in code from what the companion reports. No site knowledge.</summary>
public sealed record ElementFacts(string Ref, string Kind, string? SearchScope = null, string? SubmitRef = null,
    string? Landmark = null, bool InList = false, bool? Selected = null);

public static class BrowserDomFacts
{
    public const string SearchField = "search_field";
    /// <summary>A non-editable control that opens a search surface (a button or link named for searching).</summary>
    public const string SearchOpener = "search_opener";
    public const string SubmitControl = "submit";
    public const string NavigationLink = "nav_link";
    public const string ResultItem = "result_item";
    public const string Field = "field";
    public const string Control = "control";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex InPageWords = new(@"\b(find|search)\s+(in|on)\s+(this\s+)?page\b|\bin[- ]page\b", Options);
    private static readonly Regex FilterWords = new(@"\bfilter(s|ing)?\b", Options);
    private static readonly Regex ResourceWords = new(
        @"\b(this|current)\s+(repo(?:sitory)?|project|document|doc|file|workspace|channel|playlist|course|wiki|notebook|folder|org(?:anization)?|collection|list|profile|thread|conversation|section|category)\b",
        Options);
    private static readonly Regex CollectionWords = new(@"^\s*find\s+(a|an|your|my)\b|\b(within|in\s+(?:your|my|the))\b|\b(your|my)\s+\w+", Options);
    private static readonly Regex SearchWord = new(@"\bsearch\b", Options);
    private static readonly Regex SiteWide = new(@"\b(this\s+(?:site|website)|all\s+of|entire\s+site|everywhere|the\s+web)\b", Options);
    private static readonly Regex NotAnOpener = new(@"\b(clear|close|cancel|reset|remove|voice|history|recent)\b", Options);
    private static readonly Regex SubmitWords = new(@"^\s*(search|go|find|submit|apply|enter)\b|\bsearch\b", Options);

    public static IReadOnlyDictionary<string, ElementFacts> Derive(IReadOnlyList<BrowserElement> elements)
    {
        var submits = elements.Where(static e => e.Enabled && (e.Role is "button" or "link") && IsSubmit(e)).ToArray();
        var result = new Dictionary<string, ElementFacts>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            var scope = SearchScopeOf(element);
            var searchField = element.Editable && (element.Search || element.Role == "searchbox" || scope != SearchScopes.Unknown);
            var opener = !element.Editable && !element.Submit && (element.Role is "button" or "link")
                && SearchWord.IsMatch(element.Name ?? "") && !NotAnOpener.IsMatch(element.Name ?? "");
            var kind = searchField ? SearchField
                : opener ? SearchOpener
                : element.Editable ? Field
                : element.Submit ? SubmitControl
                : element.Role == "link" && element.Landmark == "navigation" ? NavigationLink
                : element.Role == "link" && element.InList ? ResultItem
                : Control;
            string? submitRef = null;
            if (searchField && !string.IsNullOrEmpty(element.Form))
                submitRef = submits.Where(s => s.Form == element.Form).OrderByDescending(static s => s.Submit).FirstOrDefault()?.Ref;
            result[element.Ref] = new(element.Ref, kind, searchField || opener ? scope : null, submitRef, element.Landmark,
                element.InList, element.Selected);
        }
        return result;
    }

    /// <summary>The scope a search-like control declares; Unknown when its wording says nothing about what it searches.</summary>
    public static string SearchScopeOf(BrowserElement element)
    {
        var words = $"{element.Name} {element.Value}".Trim();
        if (InPageWords.IsMatch(words)) return SearchScopes.InPage;
        if (FilterWords.IsMatch(words)) return SearchScopes.Filter;
        if (SiteWide.IsMatch(words)) return SearchScopes.Global;
        if (ResourceWords.IsMatch(words)) return SearchScopes.CurrentResource;
        if (CollectionWords.IsMatch(words)) return SearchScopes.Collection;
        // A form that posts to a deep path (/owner/repo/search) searches inside that path, not the site root.
        if (element.FormAction is { Length: > 0 } action)
        {
            var segments = action.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 || segments.Length == 2 && !segments[^1].Equals("search", StringComparison.OrdinalIgnoreCase))
                return SearchScopes.CurrentResource;
        }
        return SearchWord.IsMatch(words) || element.Role == "searchbox" ? SearchScopes.Global : SearchScopes.Unknown;
    }

    private static bool IsSubmit(BrowserElement e) => e.Submit || SubmitWords.IsMatch(e.Name ?? "");
}
