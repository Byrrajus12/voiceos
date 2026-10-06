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
        new("YouTube Music", ["youtube music", "music.youtube.com"], new("https://music.youtube.com/"), ["YouTube Music"]),
        new("GitHub", ["github", "github.com"], new("https://github.com/"), ["GitHub Desktop"]),
        new("Spotify", ["spotify", "spotify web player"], new("https://open.spotify.com/"), ["Spotify"]),
        new("Amazon", ["amazon", "amazon.com"], new("https://www.amazon.com/")),
        new("Uber Eats", ["uber eats", "ubereats", "ubereats.com"], new("https://www.ubereats.com/"), ["Uber Eats"]),
        new("Instagram", ["instagram", "instagram.com"], new("https://www.instagram.com/"), ["Instagram"]),
        new("Reddit", ["reddit", "reddit.com"], new("https://www.reddit.com/")),
        new("Gmail", ["gmail", "gmail.com", "google mail"], new("https://mail.google.com/")),
        new("Google Drive", ["google drive", "drive.google.com"], new("https://drive.google.com/"))
    ];

    public static Dictionary<string, string> DestinationChoices { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["None"] = "No explicitly named web service.",
            ["Google"] = "Explicit Google web destination.",
            ["YouTube"] = "Explicit YouTube web destination.",
            ["YouTube Music"] = "Explicit YouTube Music destination, distinct from YouTube.",
            ["GitHub"] = "Explicit GitHub web destination.",
            ["Spotify"] = "Explicit Spotify web destination, including its web player.",
            ["Amazon"] = "Explicit Amazon web destination.",
            ["Uber Eats"] = "Explicit Uber Eats web destination.",
            ["Instagram"] = "Explicit Instagram destination.",
            ["Reddit"] = "Explicit Reddit destination.",
            ["Gmail"] = "Explicit Gmail destination.",
            ["Google Drive"] = "Explicit Google Drive destination, distinct from Google search."
        };

    public static ServiceDescriptor? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
        return KnownServices.FirstOrDefault(service =>
            service.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Aliases.Any(alias => alias.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether an installed app's name identifies this service: its canonical name, a
    /// web alias, or a registered installed-app alias. A different, more specific service
    /// (YouTube Music for YouTube) never represents it.</summary>
    public static bool IsRepresentedBy(ServiceDescriptor service, string? appDisplayName)
        => !string.IsNullOrWhiteSpace(appDisplayName)
            && (Resolve(appDisplayName) == service
                || service.InstalledAppAliases?.Any(alias =>
                    alias.Equals(appDisplayName.Trim(), StringComparison.OrdinalIgnoreCase)) == true);
}
