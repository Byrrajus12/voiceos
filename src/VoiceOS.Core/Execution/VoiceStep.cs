using VoiceOS.Core.Decision;
using VoiceOS.Core.Monitors;

namespace VoiceOS.Core.Execution;

// ── Targets ──────────────────────────────────────────────────────────────────

/// <summary>Discriminated union of window/app reference kinds used in VoiceStep arguments.</summary>
public abstract record VoiceTarget;

/// <summary>Current foreground window — resolved at execution time, never at planning time.</summary>
public sealed record CurrentWindowTarget : VoiceTarget;

/// <summary>A specific window identified at planning time by its snapshot candidate ID.</summary>
public sealed record CandidateWindowTarget(string WindowCandidateId) : VoiceTarget;

/// <summary>
/// A logical app reference by catalog ID.
/// The execution layer resolves trusted identity (ProcessName, AUMID) from IAppCatalog at runtime.
/// </summary>
public sealed record AppTarget(string AppCandidateId) : VoiceTarget;

/// <summary>
/// The concrete window produced by a previous step in the same program.
/// Resolution fails if the referenced step produced no window — never falls back to foreground.
/// </summary>
public sealed record StepResultTarget(string StepId) : VoiceTarget;

// ── VoiceProgram ──────────────────────────────────────────────────────────────

/// <summary>
/// A sequentially-executed list of typed steps.
/// A single command is a one-step VoiceProgram; compound commands are multi-step.
/// </summary>
public sealed record VoiceProgram(IReadOnlyList<VoiceStep> Steps);

// ── Steps ─────────────────────────────────────────────────────────────────────

public abstract record VoiceStep(string StepId);

/// <summary>
/// Open an app (focus existing window or launch new instance).
/// Produces a concrete ResultWindow that later steps may reference via StepResultTarget.
/// </summary>
public sealed record OpenAppStep(
    string StepId,
    AppTarget App,
    AppActivationMode ActivationMode = AppActivationMode.FocusOrLaunch) : VoiceStep(StepId);

public sealed record FocusWindowStep(string StepId, VoiceTarget Target) : VoiceStep(StepId);
public sealed record CloseWindowStep(string StepId, VoiceTarget Target) : VoiceStep(StepId);
public sealed record MinimizeWindowStep(string StepId, VoiceTarget Target) : VoiceStep(StepId);
public sealed record MaximizeWindowStep(string StepId, VoiceTarget Target) : VoiceStep(StepId);

/// <summary>Snap a window to a screen side. Direction is required; invalid combos cannot be constructed.</summary>
public sealed record SnapWindowStep(string StepId, VoiceTarget Target, SnapDirection Direction) : VoiceStep(StepId);

/// <summary>
/// Move a window to a different monitor.
/// On success, produces the same window as its Result so StepResultTarget references remain valid.
/// </summary>
public sealed record MoveWindowStep(string StepId, VoiceTarget Target, MonitorTarget Monitor) : VoiceStep(StepId);

public sealed record MediaControlStep(string StepId, MediaOperation Operation) : VoiceStep(StepId);
public sealed record SetVolumeStep(string StepId, int Value) : VoiceStep(StepId);
/// <summary>
/// Adjust volume by a relative amount. When Amount is null, applies the default step.
/// When Amount is set (1–100), adjusts by that many percentage points (e.g. Amount=10 → ±0.10 scalar).
/// </summary>
public sealed record AdjustVolumeStep(string StepId, VolumeDirection Direction, int? Amount = null) : VoiceStep(StepId);
