namespace VoiceOS.Core.Decision;

public enum VoiceAction
{
    None,
    OpenApp,
    FocusWindow,
    CloseCurrentWindow,
    MaximizeCurrentWindow,
    MinimizeCurrentWindow,
    SnapCurrentWindow,
    MediaControl,
    SetVolume,
    AdjustVolume,
    Rejected
}

public enum MediaOperation { Play, Pause, Toggle, Next, Previous }
public enum SnapDirection { Left, Right }
public enum VolumeDirection { Up, Down }

public record VoicePlan(
    VoiceAction Action,
    string? AppCandidate = null,
    string? WindowCandidate = null,
    string? TextContent = null,
    MediaOperation? Media = null,
    SnapDirection? Snap = null,
    int? VolumeValue = null,
    VolumeDirection? VolumeAdjust = null,
    double Confidence = 0.0,
    bool RequiresClarification = false,
    string? RejectionReason = null);
