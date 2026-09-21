using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Handles OpenApp execution for VoiceProgram steps, returning the concrete resulting window.
///
/// FocusOrLaunchAsync: focus the existing window if exactly one matches; launch and poll otherwise.
/// LaunchNewAsync: always launch a new instance and poll for its window.
///
/// Both methods return a typed AppWindowResult. On success, ResultWindow carries the concrete
/// window candidate so later steps can reference it via StepResultTarget.
/// </summary>
public interface IWindowAwareLauncher
{
    Task<AppWindowResult> FocusOrLaunchAsync(
        AppTarget target,
        IReadOnlyList<WindowCandidate> snapshot,
        CancellationToken ct = default);

    Task<AppWindowResult> LaunchNewAsync(
        AppTarget target,
        CancellationToken ct = default);
}
