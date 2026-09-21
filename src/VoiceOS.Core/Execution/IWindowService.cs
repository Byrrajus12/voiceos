using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

public interface IWindowService
{
    /// <summary>
    /// Returns the HWND of the current foreground window, or 0 if there is none.
    /// Callers that need to operate on the foreground window must sample this once
    /// and use the same value for all subsequent operations in that step.
    /// </summary>
    nint GetForegroundWindowHwnd();

    /// <summary>Bring a specific window (by planning-time HWND) to foreground.</summary>
    ExecutionResult Focus(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot);

    /// <summary>
    /// Close a window. If <paramref name="windowCandidateId"/> is null, acts on the current
    /// foreground window. If set, acts on the named planning-time window; returns WindowStale
    /// if the HWND is no longer valid — never falls back to foreground.
    /// </summary>
    ExecutionResult Close(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot);

    /// <summary>Maximize a window. Null = foreground; named = planning-time snapshot HWND.</summary>
    ExecutionResult Maximize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot);

    /// <summary>Minimize a window. Null = foreground; named = planning-time snapshot HWND.</summary>
    ExecutionResult Minimize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot);

    /// <summary>
    /// Snap a window to a screen side. Win+Arrow acts on the foreground window, so for a
    /// named target the window is brought to foreground first, then the key combo is sent.
    /// </summary>
    ExecutionResult Snap(SnapDirection direction, string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot);
}
