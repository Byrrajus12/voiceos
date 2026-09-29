namespace VoiceOS.Core.Browser;

public enum UrlRelation
{
    /// <summary>Same URL after normalization (scheme, www, trailing slash, fragment).</summary>
    Exact,
    /// <summary>The link's URL carries the landing URL as an embedded parameter or path (generic redirector).</summary>
    Embedded,
    /// <summary>Same host or a subdomain of the link's host, but a different URL.</summary>
    SameOrigin,
    Inconsistent
}

/// <summary>Whether a landing URL is the one a link pointed to. Pure and host-list free.</summary>
public static class UrlConsistency
{
    public static UrlRelation Relate(string? href, string? landing)
    {
        if (!TryWeb(href, out var link) || !TryWeb(landing, out var page)) return UrlRelation.Inconsistent;
        if (StringComparer.Ordinal.Equals(Normalize(link), Normalize(page))) return UrlRelation.Exact;
        if (Embeds(link, page)) return UrlRelation.Embedded;
        return BrowserCompletionEvidence.IsSameSiteOrSubdomain(page.Host, link.Host)
            ? UrlRelation.SameOrigin : UrlRelation.Inconsistent;
    }

    /// <summary>Neither host is the other or a subdomain of it, and they do not share a registrable-looking suffix.</summary>
    public static bool UnrelatedHosts(string? a, string? b)
    {
        if (!TryWeb(a, out var left) || !TryWeb(b, out var right)) return false;
        if (BrowserCompletionEvidence.IsSameSiteOrSubdomain(left.Host, right.Host)
            || BrowserCompletionEvidence.IsSameSiteOrSubdomain(right.Host, left.Host)) return false;
        return Registrable(left.Host) != Registrable(right.Host);
    }

    private static string Registrable(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
    }

    private static bool TryWeb(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
            return true;
        uri = null!;
        return false;
    }

    private static string Normalize(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "/";
        return $"{host}:{uri.Port}{path}{uri.Query}";
    }

    /// <summary>The link's query values or path decode to a URL that equals the landing page (a redirector such as /url?q=…).</summary>
    private static bool Embeds(Uri link, Uri page)
    {
        var target = Normalize(page);
        foreach (var candidate in EmbeddedUrls(link))
            if (TryWeb(candidate, out var inner) && StringComparer.Ordinal.Equals(Normalize(inner), target))
                return true;
        return false;
    }

    private static IEnumerable<string> EmbeddedUrls(Uri link)
    {
        foreach (var pair in link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var value = SafeUnescape(pair[(eq + 1)..].Replace('+', ' '));
            if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) yield return value;
        }
        var path = SafeUnescape(link.AbsolutePath);
        var index = path.IndexOf("http", StringComparison.OrdinalIgnoreCase);
        if (index > 0) yield return path[index..];
    }

    private static string SafeUnescape(string text)
    {
        try { return Uri.UnescapeDataString(text); } catch { return text; }
    }
}
