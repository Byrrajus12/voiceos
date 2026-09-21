using Microsoft.Extensions.Logging;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;

namespace VoiceOS.Core.Execution;

/// <summary>
/// Executes a VoiceProgram as an ordered sequence of typed steps.
///
/// Maintains ProgramExecutionContext for the lifetime of the program only (no cross-utterance state).
/// A failed step stops the program — no rollback, no parallel execution, no DAG.
/// Step-result references resolve to concrete windows recorded by earlier steps.
/// </summary>
public sealed class ProgramExecutor
{
    private readonly IWindowAwareLauncher _launcher;
    private readonly IAppCatalog _catalog;
    private readonly IWindowService _windows;
    private readonly IMediaService _media;
    private readonly IVolumeService _volume;
    private readonly ILogger<ProgramExecutor> _logger;

    public ProgramExecutor(
        IWindowAwareLauncher launcher,
        IAppCatalog catalog,
        IWindowService windows,
        IMediaService media,
        IVolumeService volume,
        ILogger<ProgramExecutor> logger)
    {
        _launcher = launcher;
        _catalog = catalog;
        _windows = windows;
        _media = media;
        _volume = volume;
        _logger = logger;
    }

    public async Task<ProgramResult> ExecuteAsync(
        VoiceProgram program,
        IReadOnlyList<WindowCandidate> snapshot,
        CancellationToken ct = default)
    {
        var context = new ProgramExecutionContext();
        var results = new List<VoiceStepResult>();
        // Grows as steps produce new windows not in the planning-time snapshot
        var effectiveSnapshot = snapshot.ToList();

        foreach (var step in program.Steps)
        {
            var stepResult = await ExecuteStepAsync(step, effectiveSnapshot, context, ct);
            results.Add(stepResult);
            context.Record(stepResult);

            // Store the result window under a stable step-scoped ID so later steps can
            // reference it via StepResultTarget without cross-snapshot candidate-ID collisions.
            // Candidate IDs like "w0" are enumeration-order artifacts of a single snapshot call;
            // they must not be used as cross-snapshot identity.
            if (stepResult.ResultWindow is { } w)
                effectiveSnapshot.Add(w with { Id = $"result:{stepResult.StepId}" });

            if (!stepResult.Succeeded)
                break;
        }

        _logger.LogInformation("Program complete: {Executed}/{Total} steps ok={AllOk}",
            results.Count, program.Steps.Count, results.All(r => r.Succeeded));

        return new ProgramResult(results);
    }

    private async Task<VoiceStepResult> ExecuteStepAsync(
        VoiceStep step,
        List<WindowCandidate> effectiveSnapshot,
        ProgramExecutionContext context,
        CancellationToken ct)
    {
        switch (step)
        {
            case OpenAppStep openApp:
                return await ExecuteOpenAppAsync(openApp, effectiveSnapshot, ct);

            case FocusWindowStep { Target: var t } s:
                return ExecuteWindowOp(s, t, effectiveSnapshot, context,
                    (id, snap) => _windows.Focus(id, snap));

            case CloseWindowStep { Target: var t } s:
                return ExecuteWindowOp(s, t, effectiveSnapshot, context,
                    (id, snap) => _windows.Close(id, snap));

            case MinimizeWindowStep { Target: var t } s:
                return ExecuteWindowOp(s, t, effectiveSnapshot, context,
                    (id, snap) => _windows.Minimize(id, snap));

            case MaximizeWindowStep { Target: var t } s:
                return ExecuteWindowOp(s, t, effectiveSnapshot, context,
                    (id, snap) => _windows.Maximize(id, snap));

            case SnapWindowStep snapStep:
                return ExecuteWindowOp(snapStep, snapStep.Target, effectiveSnapshot, context,
                    (id, snap) => _windows.Snap(snapStep.Direction, id, snap));

            case MediaControlStep media:
                return ExecuteSimple(media.StepId, _media.Send(media.Operation));

            case SetVolumeStep vol:
                return ExecuteSimple(vol.StepId, _volume.SetVolume(vol.Value));

            case AdjustVolumeStep adj:
                return ExecuteSimple(adj.StepId, _volume.AdjustVolume(adj.Direction));

            default:
                return VoiceStepResult.ExecutionFailed(step.StepId,
                    $"Unknown step type: {step.GetType().Name}");
        }
    }

    private async Task<VoiceStepResult> ExecuteOpenAppAsync(
        OpenAppStep step,
        List<WindowCandidate> effectiveSnapshot,
        CancellationToken ct)
    {
        var result = step.ActivationMode == AppActivationMode.NewInstance
            ? await _launcher.LaunchNewAsync(step.App, ct)
            : await _launcher.FocusOrLaunchAsync(step.App, effectiveSnapshot, ct);

        _logger.LogInformation("OpenApp '{App}' → {Status}", step.App.AppCandidateId, result.Status);

        return result.Succeeded
            ? VoiceStepResult.Ok(step.StepId, result.ResultWindow, result.Detail)
            : VoiceStepResult.ExecutionFailed(step.StepId, result.Detail ?? result.Status.ToString());
    }

    private VoiceStepResult ExecuteWindowOp(
        VoiceStep step,
        VoiceTarget target,
        List<WindowCandidate> effectiveSnapshot,
        ProgramExecutionContext context,
        Func<string?, IReadOnlyList<WindowCandidate>, ExecutionResult> operation)
    {
        var (windowId, resolvedWindow, error) = ResolveWindowTarget(target, effectiveSnapshot, context);
        if (error != null)
            return VoiceStepResult.ResolutionFailed(step.StepId, error);

        var result = operation(windowId, effectiveSnapshot);
        _logger.LogInformation("WindowOp '{Step}' → {Status}", step.StepId, result.Status);

        return result.Status == ExecutionStatus.Success
            ? VoiceStepResult.Ok(step.StepId, resolvedWindow, result.Detail)
            : VoiceStepResult.ExecutionFailed(step.StepId, result.Detail ?? result.Status.ToString());
    }

    private static VoiceStepResult ExecuteSimple(string stepId, ExecutionResult result)
        => result.Status == ExecutionStatus.Success
            ? VoiceStepResult.Ok(stepId, null, result.Detail)
            : VoiceStepResult.ExecutionFailed(stepId, result.Detail ?? result.Status.ToString());

    /// <summary>
    /// Resolves a VoiceTarget to a concrete window candidate.
    /// Returns (candidateId, window, null) on success; (null, null, error) on failure.
    /// null candidateId means "current foreground window" — passed as-is to IWindowService.
    /// </summary>
    private (string? windowId, WindowCandidate? window, string? error) ResolveWindowTarget(
        VoiceTarget target,
        List<WindowCandidate> effectiveSnapshot,
        ProgramExecutionContext context)
    {
        switch (target)
        {
            case CurrentWindowTarget:
                return (null, null, null);

            case CandidateWindowTarget { WindowCandidateId: var id }:
                var c = effectiveSnapshot.FirstOrDefault(w => w.Id == id);
                return c != null ? (id, c, null) : (null, null, $"Window candidate '{id}' not found in snapshot");

            case AppTarget { AppCandidateId: var appId }:
                var entry = _catalog.FindById(appId);
                if (entry == null)
                    return (null, null, $"App '{appId}' not found in catalog");
                var matches = AppWindowMatcher.FindMatching(entry.ProcessName, entry.AppUserModelId, effectiveSnapshot);
                return matches.Count switch
                {
                    1 => (matches[0].Id, matches[0], null),
                    0 => (null, null, $"No window found for app '{appId}'"),
                    _ => (null, null, $"Multiple windows for app '{appId}' — ambiguous")
                };

            case StepResultTarget { StepId: var sid }:
                var prev = context.Get(sid);
                if (prev == null)
                    return (null, null, $"Step '{sid}' has no recorded result");
                if (prev.ResultWindow == null)
                    return (null, null, $"Step '{sid}' did not produce a window");
                // Resolve via stable step-scoped ID — never via the snapshot-local candidate ID,
                // which may collide with IDs from a different snapshot enumeration.
                var stableId = $"result:{sid}";
                var resultEntry = effectiveSnapshot.FirstOrDefault(w => w.Id == stableId);
                return resultEntry != null
                    ? (stableId, resultEntry, null)
                    : (null, null, $"Step '{sid}' result window not found in effective snapshot");

            default:
                return (null, null, $"Unknown target type: {target.GetType().Name}");
        }
    }

    private sealed class ProgramExecutionContext
    {
        private readonly Dictionary<string, VoiceStepResult> _results = [];
        public void Record(VoiceStepResult r) => _results[r.StepId] = r;
        public VoiceStepResult? Get(string stepId) => _results.GetValueOrDefault(stepId);
    }
}
