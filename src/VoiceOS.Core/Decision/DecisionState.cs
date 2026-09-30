using VoiceOS.Core.Candidates;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Decision;

public record DecisionState(
    string Transcript,
    string ForegroundApp,
    IReadOnlyList<AppCandidate> InstalledApps,
    IReadOnlyList<WindowCandidate> OpenWindows,
    IReadOnlyList<MediaOperation> AvailableMediaOps,
    IReadOnlyList<SnapDirection> AvailableSnapDirs,
    DisplayTopology? Topology = null,
    IReadOnlyList<string>? ReferentWindowIds = null,
    /// <summary>The latest window VoiceOS used has since closed, so an implicit reference may be continuing it.</summary>
    bool StaleReferentWindow = false);
