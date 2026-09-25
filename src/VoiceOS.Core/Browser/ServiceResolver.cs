namespace VoiceOS.Core.Browser;

/// <summary>Trusted service identity and location, with no site workflow knowledge.</summary>
public sealed record ServiceDescriptor(
    string CanonicalName, IReadOnlyList<string> Aliases, Uri WebOrigin,
    IReadOnlyList<string>? InstalledAppAliases = null);

public static class ServiceResolver
{
    private static readonly ServiceDescriptor[] KnownServices =
    [
        new("Google", ["google", "google.com"], new("https://www.google.com/")),
        new("YouTube", ["youtube", "youtube.com"], new("https://www.youtube.com/")),
        new("GitHub", ["github", "github.com"], new("https://github.com/")),
        new("Spotify", ["spotify", "spotify web player"], new("https://open.spotify.com/"), ["Spotify"]),
        new("Amazon", ["amazon", "amazon.com"], new("https://www.amazon.com/")),
        new("Uber Eats", ["uber eats", "ubereats", "ubereats.com"], new("https://www.ubereats.com/"), ["Uber Eats"])
    ];

    public static Dictionary<string, string> DestinationChoices { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["None"] = "No explicitly named web service.",
            ["Google"] = "Explicit Google web destination.",
            ["YouTube"] = "Explicit YouTube web destination.",
            ["GitHub"] = "Explicit GitHub web destination.",
            ["Spotify"] = "Explicit Spotify web destination, including its web player.",
            ["Amazon"] = "Explicit Amazon web destination.",
            ["Uber Eats"] = "Explicit Uber Eats web destination."
        };

    public static ServiceDescriptor? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
        return KnownServices.FirstOrDefault(service =>
            service.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Aliases.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }
}
