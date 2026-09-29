namespace VoiceOS.Core.Browser;

/// <summary>
/// Minimal browser framing. The utterance remains authoritative; these fields are
/// deterministic hints and never a substitute semantic plan.
/// </summary>
public sealed record BrowserGoal(
    string OriginalUtterance,
    Uri? ExplicitUrl = null,
    string? NamedServiceHint = null,
    BrowserGoalNormalization? Normalization = null,
    Uri? ScopedDestination = null)
{
    public static BrowserGoal FromUtterance(string utterance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(utterance);
        return new(utterance.Trim());
    }

    internal static string BootstrapUrl(BrowserGoal goal)
        => goal.ExplicitUrl?.AbsoluteUri
           ?? goal.ScopedDestination?.AbsoluteUri
           ?? (ServiceResolver.Resolve(goal.NamedServiceHint)
               ?? ServiceResolver.Resolve(goal.Normalization?.PreferredService))?.WebOrigin.AbsoluteUri
           ?? NormalizedServiceRoot(goal.Normalization)?.AbsoluteUri
           ?? "https://www.google.com/";

    internal static Uri? NormalizedServiceRoot(BrowserGoalNormalization? normalized)
        => normalized is not null && !string.IsNullOrWhiteSpace(normalized.PreferredService)
            && Uri.TryCreate(normalized.PreferredServiceUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.HostNameType == UriHostNameType.Dns
            && ServiceUrlCorroborates(normalized.PreferredService, uri) ? uri : null;

    internal static bool ServiceUrlCorroborates(string service, Uri uri)
    {
        static string Compact(string text) => new(text.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var labels = uri.Host.Split('.');
        if (labels.Length < 2) return false;
        var host = Compact(labels[^2]);
        var name = Compact(service);
        return host.Length > 0 && name.Length > 0
            && (host.Contains(name, StringComparison.Ordinal) || name.Contains(host, StringComparison.Ordinal));
    }
}

public sealed record BrowserTextCandidate(string Id, string Text);

public sealed record BrowserCorrectedTerm(string Heard, string Interpreted, double Confidence);

public sealed record BrowserGoalNormalization(
    string Objective, string? Entity, string? ResourceType, string? PreferredService,
    string? PreferredServiceUrl, IReadOnlyList<string> SearchQueries,
    string CompletionHint, IReadOnlyList<BrowserCorrectedTerm> CorrectedTerms,
    SemanticEndState EndState = SemanticEndState.OtherBoundedGoal,
    string? Descriptor = null, bool UnresolvedReference = false);

/// <summary>Produces a small set of exact substrings; it never generates prose.</summary>
public static class BrowserTextCandidates
{
    private const int Limit = 6;
    public static IReadOnlyList<BrowserTextCandidate> From(BrowserGoal goal)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value)
        {
            value = value.Trim(' ', '"', '\'', '.', ',', ';', '!', '?');
            if (value.Length is > 0 and <= 240 && seen.Add(value))
                values.Add(value);
        }

        if (goal.Normalization is { } normalized)
        {
            foreach (var query in normalized.SearchQueries.Take(3)) Add(query);
            if (normalized.Entity is not null) Add(normalized.Entity);
            return values.Select((text, index) => new BrowserTextCandidate($"t{index + 1}", text)).ToArray();
        }

        return [];
    }
}
