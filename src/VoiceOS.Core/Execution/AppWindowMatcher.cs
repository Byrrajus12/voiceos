using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Identity matching: finds open windows that correspond to an app's trusted identity.
///
/// Precedence:
///   1. App has AUMID + window has AUMID  → require exact equality; mismatch is a hard reject.
///   2. App has AUMID + window has no AUMID + owning process has package identity → require exact
///      equality with the process AUMID. Packaged desktop apps often mark only the process, and
///      their window's process need not be the manifest executable (Store Spotify declares
///      SpotifyMigrator.exe; Spotify.exe owns the window).
///   3. App has AUMID + no runtime AUMID at all → missing identity is not contradiction; use ProcessName fallback.
///   4. App has no AUMID → match by ProcessName.
/// </summary>
public static class AppWindowMatcher
{
    public static IReadOnlyList<WindowCandidate> FindMatching(
        string? processName,
        string? aumid,
        IReadOnlyList<WindowCandidate> snapshot)
    {
        if (aumid != null)
        {
            return snapshot
                .Where(w =>
                {
                    if (w.AppUserModelId != null)
                        return string.Equals(w.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase);
                    if (w.ProcessAppUserModelId != null)
                        return string.Equals(w.ProcessAppUserModelId, aumid, StringComparison.OrdinalIgnoreCase);
                    return processName != null &&
                           string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        if (processName != null)
        {
            return snapshot
                .Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return [];
    }

    /// <summary>Processes that own the windows of hosted launches. A host is never the app's own
    /// identity: it only confirms a window that appears new after the launch.</summary>
    public static IReadOnlyList<string> HostProcesses(AppHostKind host) => host switch
    {
        AppHostKind.ShellNamespace => ["explorer"],
        AppHostKind.ConsoleHosted => ["WindowsTerminal", "OpenConsole", "conhost"],
        _ => []
    };

    /// <summary>Windows a launch of this entry can produce: its own identity, plus windows of its
    /// host process for shell-namespace items and console programs. Callers compare against a
    /// pre-launch snapshot so pre-existing host windows are never claimed.</summary>
    public static IReadOnlyList<WindowCandidate> FindLaunchCandidates(AppEntry entry,
        IReadOnlyList<WindowCandidate> snapshot)
    {
        var own = FindMatching(entry.ProcessName, entry.AppUserModelId, snapshot);
        var hosts = HostProcesses(entry.Host);
        if (hosts.Count == 0) return own;
        return own.Concat(snapshot.Where(w => hosts.Contains(w.ProcessName, StringComparer.OrdinalIgnoreCase)))
            .Distinct().ToList();
    }
}
