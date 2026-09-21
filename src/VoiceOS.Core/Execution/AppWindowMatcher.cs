using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Identity matching: finds open windows that correspond to an app's trusted identity.
///
/// Precedence:
///   1. App has AUMID + window has AUMID  → require exact equality; mismatch is a hard reject.
///   2. App has AUMID + window has no AUMID → missing runtime AUMID is not contradiction; use ProcessName fallback.
///   3. App has no AUMID → match by ProcessName.
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
}
