namespace VoiceOS.Core.Candidates;

/// <param name="AppUserModelId">The window's own AUMID property (e.g. a Chrome PWA or a UWP frame).</param>
/// <param name="ProcessAppUserModelId">The package identity Windows assigned to the owning process;
/// null for unpackaged processes. Packaged desktop apps (e.g. Store Spotify) often carry identity
/// only here, not on the window.</param>
public record WindowCandidate(
    string Id,
    string ProcessName,
    string Title,
    bool IsForeground = false,
    nint Hwnd = 0,
    string? AppUserModelId = null,
    string? ExecutablePath = null,
    string? ProcessAppUserModelId = null);
