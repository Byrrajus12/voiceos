using System.Text.Json;

namespace VoiceOS.Core.Apps;

/// <summary>
/// Resolves Chrome new-instance launch arguments using profile.last_used from Chrome's Local State.
/// Read happens at launch time (not at catalog startup) so the profile reflects the user's
/// most recent Chrome session.
///
/// Returns: --profile-directory="&lt;last_used&gt;" --new-window
///   --profile-directory suppresses Chrome's profile picker (Chromium startup logic explicitly
///   skips the picker when this flag is present, matching the Jump List "New window" behavior).
///   --new-window forces a new browser window even when the profile already has one open.
///
/// Fallback: --profile-directory="Default" --new-window when Local State is absent or unreadable.
/// Non-Chrome entries: returns entry.NewInstanceArguments unchanged.
/// </summary>
public sealed class ChromeNewInstanceArgsProvider : INewInstanceArgsProvider
{
    private readonly string _localStatePath;

    public ChromeNewInstanceArgsProvider(string? localStatePath = null)
    {
        _localStatePath = localStatePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "User Data", "Local State");
    }

    public string? GetNewInstanceArguments(AppEntry entry)
    {
        if (!string.Equals(entry.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
            return entry.NewInstanceArguments;

        var profileDir = ResolveLastUsedProfile();
        return $"--profile-directory=\"{profileDir}\" --new-window";
    }

    internal string ResolveLastUsedProfile()
    {
        try
        {
            if (!File.Exists(_localStatePath))
                return "Default";

            using var stream = File.OpenRead(_localStatePath);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("last_used", out var lastUsed))
            {
                var dir = lastUsed.GetString();
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
        }
        catch { /* fall through to default */ }

        return "Default";
    }
}
