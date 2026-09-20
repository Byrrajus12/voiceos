using VoiceOS.Core.Candidates;

namespace VoiceOS.Core.Decision;

public record DecisionState(
    string Transcript,
    string ForegroundApp,
    IReadOnlyList<AppCandidate> InstalledApps,
    IReadOnlyList<WindowCandidate> OpenWindows,
    IReadOnlyList<MediaOperation> AvailableMediaOps,
    IReadOnlyList<SnapDirection> AvailableSnapDirs);
