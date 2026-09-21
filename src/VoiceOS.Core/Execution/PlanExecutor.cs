using Microsoft.Extensions.Logging;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Routes a VoicePlan to the appropriate execution service.
/// None, Rejected, and RequiresClarification plans never cause side effects.
/// </summary>
public sealed class PlanExecutor
{
    private readonly IAppLauncher _launcher;
    private readonly IWindowService _windows;
    private readonly IMediaService _media;
    private readonly IVolumeService _volume;
    private readonly ILogger<PlanExecutor> _logger;

    public PlanExecutor(
        IAppLauncher launcher,
        IWindowService windows,
        IMediaService media,
        IVolumeService volume,
        ILogger<PlanExecutor> logger)
    {
        _launcher = launcher;
        _windows = windows;
        _media = media;
        _volume = volume;
        _logger = logger;
    }

    public Task<ExecutionResult> ExecuteAsync(
        VoicePlan plan,
        IReadOnlyList<WindowCandidate> windowSnapshot,
        CancellationToken ct = default)
    {
        var result = Execute(plan, windowSnapshot);
        _logger.LogInformation("Execution: {Action} → {Status} ({Detail})",
            plan.Action, result.Status, result.Detail ?? "-");
        return Task.FromResult(result);
    }

    private ExecutionResult Execute(VoicePlan plan, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (plan.Action is VoiceAction.None or VoiceAction.Rejected)
            return ExecutionResult.Noop(plan.Action.ToString());

        if (plan.RequiresClarification)
            return ExecutionResult.Noop("RequiresClarification");

        // TODO(M5): single-plan architecture; compound VoiceProgram multi-step belongs in M5.
        return plan.Action switch
        {
            VoiceAction.OpenApp when plan.ActivationMode == AppActivationMode.NewInstance =>
                _launcher.Launch(plan.AppCandidateId),

            VoiceAction.OpenApp =>
                FocusOrLaunch(plan, snapshot),

            VoiceAction.FocusWindow =>
                _windows.Focus(plan.WindowCandidateId, snapshot),

            VoiceAction.CloseCurrentWindow =>
                _windows.Close(plan.WindowCandidateId, snapshot),

            VoiceAction.MaximizeCurrentWindow =>
                _windows.Maximize(plan.WindowCandidateId, snapshot),

            VoiceAction.MinimizeCurrentWindow =>
                _windows.Minimize(plan.WindowCandidateId, snapshot),

            VoiceAction.SnapCurrentWindow when plan.Snap.HasValue =>
                _windows.Snap(plan.Snap.Value, plan.WindowCandidateId, snapshot),

            VoiceAction.MediaControl when plan.Media.HasValue =>
                _media.Send(plan.Media.Value),

            VoiceAction.SetVolume when plan.VolumeValue.HasValue =>
                _volume.SetVolume(plan.VolumeValue.Value),

            VoiceAction.AdjustVolume when plan.VolumeAdjust.HasValue =>
                _volume.AdjustVolume(plan.VolumeAdjust.Value),

            _ => ExecutionResult.Fail(ExecutionStatus.NoAction, $"Incomplete plan for {plan.Action}")
        };
    }

    /// <summary>
    /// Implements FocusOrLaunch semantics: focus a matching open window when exactly one exists;
    /// launch when none exist; preserve honest ambiguity when multiple windows match.
    ///
    /// Identity matching precedence:
    ///   1. App has AUMID + window has AUMID:
    ///      Equal   → strong match.
    ///      Unequal → HARD REJECT. ProcessName is never consulted after a known AUMID mismatch.
    ///   2. App has AUMID + window has no AUMID:
    ///      Missing runtime AUMID is not contradictory evidence. Use ProcessName fallback.
    ///      Required for VS Code, which sets no per-window AUMID at runtime.
    ///   3. App has no AUMID:
    ///      Use ProcessName fallback as before.
    /// </summary>
    private ExecutionResult FocusOrLaunch(VoicePlan plan, IReadOnlyList<WindowCandidate> snapshot)
    {
        if (string.IsNullOrEmpty(plan.AppCandidateId))
            return ExecutionResult.Fail(ExecutionStatus.AppNotFound, "No app candidate ID");

        List<WindowCandidate> matching;

        if (plan.AppUserModelId is { } appAumid)
        {
            matching = snapshot
                .Where(w =>
                {
                    if (w.AppUserModelId != null)
                        // Both sides have identity — require exact equality; mismatch is a hard reject.
                        return string.Equals(w.AppUserModelId, appAumid, StringComparison.OrdinalIgnoreCase);
                    // Window has no runtime AUMID — missing evidence, not contradiction.
                    // Fall back to ProcessName if available.
                    return plan.AppProcessName != null &&
                           string.Equals(w.ProcessName, plan.AppProcessName, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
        else if (plan.AppProcessName is { } procName)
        {
            // App has no AUMID: match by ProcessName.
            matching = snapshot
                .Where(w => string.Equals(w.ProcessName, procName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            matching = [];
        }

        if (matching.Count == 1)
        {
            _logger.LogInformation("FocusOrLaunch: focusing existing {App} window", plan.AppCandidate);
            return _windows.Focus(matching[0].Id, snapshot);
        }

        if (matching.Count > 1)
        {
            // Multiple windows open — do not silently pick one.
            _logger.LogInformation("FocusOrLaunch: multiple {App} windows — ambiguous", plan.AppCandidate);
            return ExecutionResult.Fail(ExecutionStatus.NoAction,
                $"Multiple {plan.AppCandidate} windows are open. Use 'switch to [window]' to pick one.");
        }

        _logger.LogInformation("FocusOrLaunch: launching {App}", plan.AppCandidate);
        return _launcher.Launch(plan.AppCandidateId);
    }
}
