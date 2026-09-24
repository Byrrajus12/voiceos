namespace VoiceOS.Core.Browser;

/// <summary>Trusted service identity and location, with no site workflow knowledge.</summary>
public sealed record ServiceDescriptor(
    string CanonicalName, IReadOnlyList<string> Aliases, Uri WebOrigin,
    IReadOnlyList<string>? InstalledAppAliases = null);

public static class ServiceResolver
{
    private static readonly ServiceDescriptor[] KnownServices =
    [
        new("YouTube", ["youtube", "youtube.com"], new("https://www.youtube.com/")),
        new("GitHub", ["github", "github.com"], new("https://github.com/")),
        new("Spotify", ["spotify", "spotify web player"], new("https://open.spotify.com/"), ["Spotify"]),
        new("Amazon", ["amazon", "amazon.com"], new("https://www.amazon.com/")),
        new("Uber Eats", ["uber eats", "ubereats", "ubereats.com"], new("https://www.ubereats.com/"), ["Uber Eats"])
    ];

    public static ServiceDescriptor? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
        return KnownServices.FirstOrDefault(service =>
            service.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Aliases.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }
}
