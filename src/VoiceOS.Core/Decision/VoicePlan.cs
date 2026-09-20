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
public enum AppActivationMode { FocusOrLaunch, NewInstance }

/// <summary>
/// Whether the user intends the foreground/current window or a specific named app window.
/// Current → null WindowCandidateId is valid (routes to foreground).
/// Named → a resolved WindowCandidateId is required; missing → RequiresClarification, never fall back to foreground.
/// </summary>
public enum WindowTargetMode { Current, Named }

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
    string? RejectionReason = null,
    string? AppCandidateId = null,
    string? WindowCandidateId = null,
    string? AppProcessName = null,
    AppActivationMode ActivationMode = AppActivationMode.FocusOrLaunch,
    WindowTargetMode WindowTargetMode = WindowTargetMode.Current);
