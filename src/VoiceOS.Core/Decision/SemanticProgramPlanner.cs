using VoiceOS.Core.Candidates;
using VoiceOS.Core.Execution;

namespace VoiceOS.Core.Decision;

/// <summary>
/// Builds a VoiceProgram from Jev answers and a DecisionState.
///
/// Handles both single-action requests (one-step VoiceProgram) and compound utterances
/// (multi-step VoiceProgram) using bounded per-unit questions from the Jev response.
///
/// Compound structure is derived from semantic unit answers (unit_N_*), not from regex
/// or text splitting. The executor remains entirely unaware of natural-language pronouns.
///
/// Prior-step references ('it', 'that') become StepResultTarget only when:
///   - unit_N_prior_ref noul >= 0.5, AND
///   - a prior step that can yield a window exists.
/// If prior_ref is requested but no valid window-yielding step exists, returns null
/// (signals clarification needed rather than silently falling back to foreground).
/// </summary>
public sealed class SemanticProgramPlanner
{
    public const int MaxUnits = 4;

    /// <summary>
    /// Decision boundary for noul-type Jev answers (probability ≥ this → true).
    /// Noul is a probability in [0,1]; 0.5 is the neutral midpoint, not a quality threshold.
    /// </summary>
    internal const double NoulDecisionBoundary = 0.5;

    private readonly double _commandThreshold;
    private readonly double _actionThreshold;

    public SemanticProgramPlanner(double commandThreshold, double actionThreshold)
    {
        _commandThreshold = commandThreshold;
        _actionThreshold = actionThreshold;
    }

    /// <summary>
    /// Build a VoiceProgram from Jev answers.
    /// Returns null when: not a command, compound structure is invalid, or a prior-step
    /// reference resolves to a step that cannot yield a window (safe clarification path).
    /// </summary>
    public VoiceProgram? TryBuildProgram(
        Dictionary<string, JevAnswer> answers,
        DecisionState state,
        IReadOnlyDictionary<string, string> textCandidates)
    {
        // Gate: is_command noul must pass threshold
        if (!answers.TryGetValue("is_command", out var isCmd)) return null;
        double isCmdProb = isCmd.QuestionType == "noul"
            ? isCmd.Probabilities.GetValueOrDefault("noul")
            : isCmd.Confidence;
        if (isCmdProb < _commandThreshold) return null;

        int unitCount = DetermineUnitCount(answers);
        var steps = new List<VoiceStep>(unitCount);

        for (int i = 1; i <= Math.Min(unitCount, MaxUnits); i++)
        {
            var unitAnswers = ExtractUnitAnswers(answers, i);

            StepResultTarget? priorRef = null;
            if (i > 1 && IsPriorRefConfident(answers, i))
            {
                var prevWindow = steps.LastOrDefault(CanProduceWindow);
                if (prevWindow == null)
                {
                    // Prior-step reference requested, but no window-yielding step precedes this one.
                    // Return null rather than silently falling back to foreground.
                    return null;
                }
                priorRef = new StepResultTarget(prevWindow.StepId);
            }

            var step = BuildUnitStep($"s{i}", unitAnswers, state, priorRef);
            if (step == null)
            {
                if (i == 1) return null;
                break;
            }
            steps.Add(step);
        }

        return steps.Count > 0 ? new VoiceProgram(steps) : null;
    }

    /// <summary>
    /// Converts a single VoicePlan (from the backward-compat flat-answer path) into
    /// a one-step VoiceProgram. Used as a fallback when unit_N answers are absent.
    /// </summary>
    public static VoiceProgram? ToSingleStepProgram(VoicePlan plan)
    {
        if (plan.RequiresClarification || plan.Action is VoiceAction.None or VoiceAction.Rejected)
            return null;

        var step = PlanToStep("s1", plan);
        return step != null ? new VoiceProgram([step]) : null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private int DetermineUnitCount(Dictionary<string, JevAnswer> answers)
    {
        if (answers.TryGetValue("unit_count", out var countAnswer)
            && countAnswer.SelectedChoice != null
            && int.TryParse(countAnswer.SelectedChoice, out int n)
            && n >= 1)
        {
            return n;
        }

        // Fallback: is_compound noul < NoulDecisionBoundary → single
        if (answers.TryGetValue("is_compound", out var compound)
            && compound.QuestionType == "noul"
            && compound.Probabilities.GetValueOrDefault("noul") < NoulDecisionBoundary)
        {
            return 1;
        }

        return 1;
    }

    private bool IsPriorRefConfident(Dictionary<string, JevAnswer> answers, int unit)
    {
        var key = $"unit_{unit}_prior_ref";
        return answers.TryGetValue(key, out var ans)
            && ans.QuestionType == "noul"
            && ans.Probabilities.GetValueOrDefault("noul") >= NoulDecisionBoundary;
    }

    private static bool CanProduceWindow(VoiceStep step) => step is
        OpenAppStep or
        FocusWindowStep or
        CloseWindowStep or
        MinimizeWindowStep or
        MaximizeWindowStep or
        SnapWindowStep;

    // Strips "unit_N_" prefix from answer keys, giving a flat view for BuildUnitStep.
    private static Dictionary<string, JevAnswer> ExtractUnitAnswers(
        Dictionary<string, JevAnswer> answers, int unit)
    {
        var prefix = $"unit_{unit}_";
        var result = new Dictionary<string, JevAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in answers)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result[kv.Key[prefix.Length..]] = kv.Value;
        }
        return result;
    }

    private VoiceStep? BuildUnitStep(
        string stepId,
        Dictionary<string, JevAnswer> unitAnswers,
        DecisionState state,
        StepResultTarget? priorRef)
    {
        if (!unitAnswers.TryGetValue("action_kind", out var kindAnswer)
            || kindAnswer.SelectedChoice == null)
            return null;

        if (!Enum.TryParse<VoiceAction>(kindAnswer.SelectedChoice, out var action)
            || action is VoiceAction.None or VoiceAction.Rejected)
            return null;

        if (kindAnswer.Confidence < _actionThreshold)
            return null;

        // App candidate (used for OpenApp and Named window ops)
        string? appCandidateId = null;
        if (unitAnswers.TryGetValue("target_app", out var appAnswer) && appAnswer.SelectedChoice != null)
            appCandidateId = state.InstalledApps.FirstOrDefault(a => a.Id == appAnswer.SelectedChoice)?.Id;

        // Window target mode
        WindowTargetMode windowMode = WindowTargetMode.Current;
        if (unitAnswers.TryGetValue("window_target_mode", out var wtm) && wtm.SelectedChoice != null)
            windowMode = wtm.SelectedChoice == "Named" ? WindowTargetMode.Named : WindowTargetMode.Current;

        VoiceTarget BuildWindowTarget()
        {
            if (priorRef != null) return priorRef;
            if (windowMode == WindowTargetMode.Named && appCandidateId != null)
                return new AppTarget(appCandidateId);
            return new CurrentWindowTarget();
        }

        // Snap direction
        SnapDirection? snapDir = null;
        if (unitAnswers.TryGetValue("snap_dir", out var snapAnswer) && snapAnswer.SelectedChoice != null
            && Enum.TryParse<SnapDirection>(snapAnswer.SelectedChoice, out var parsedSnap))
        {
            snapDir = parsedSnap;
        }

        // Activation mode
        AppActivationMode activationMode = AppActivationMode.FocusOrLaunch;
        if (unitAnswers.TryGetValue("activation_mode", out var modeAnswer) && modeAnswer.SelectedChoice != null)
            Enum.TryParse(modeAnswer.SelectedChoice, out activationMode);

        // Volume direction
        VolumeDirection? volDir = null;
        if (unitAnswers.TryGetValue("volume_direction", out var volAnswer) && volAnswer.SelectedChoice != null
            && Enum.TryParse<VolumeDirection>(volAnswer.SelectedChoice, out var parsedVol))
        {
            volDir = parsedVol;
        }

        // Media operation
        MediaOperation? mediaOp = null;
        if (unitAnswers.TryGetValue("media_op", out var mediaAnswer) && mediaAnswer.SelectedChoice != null
            && Enum.TryParse<MediaOperation>(mediaAnswer.SelectedChoice, out var parsedOp))
        {
            mediaOp = parsedOp;
        }

        // Volume value (deterministic — VoiceOS-owned, not LLM-generated)
        int? volumeValue = null;
        if (action == VoiceAction.SetVolume
            && VolumeExtractor.TryExtractPercent(state.Transcript, out int pct))
        {
            volumeValue = pct;
        }

        return action switch
        {
            VoiceAction.OpenApp when appCandidateId != null =>
                new OpenAppStep(stepId, new AppTarget(appCandidateId), activationMode),

            VoiceAction.FocusWindow =>
                new FocusWindowStep(stepId, BuildWindowTarget()),

            VoiceAction.CloseCurrentWindow =>
                new CloseWindowStep(stepId, BuildWindowTarget()),

            VoiceAction.MaximizeCurrentWindow =>
                new MaximizeWindowStep(stepId, BuildWindowTarget()),

            VoiceAction.MinimizeCurrentWindow =>
                new MinimizeWindowStep(stepId, BuildWindowTarget()),

            VoiceAction.SnapCurrentWindow when snapDir.HasValue =>
                new SnapWindowStep(stepId, BuildWindowTarget(), snapDir.Value),

            VoiceAction.MediaControl when mediaOp.HasValue =>
                new MediaControlStep(stepId, mediaOp.Value),

            VoiceAction.SetVolume when volumeValue.HasValue =>
                new SetVolumeStep(stepId, volumeValue.Value),

            VoiceAction.AdjustVolume when volDir.HasValue =>
                new AdjustVolumeStep(stepId, volDir.Value),

            _ => null
        };
    }

    // ── VoicePlan → VoiceStep adapter (single-unit fallback) ─────────────────────

    private static VoiceStep? PlanToStep(string stepId, VoicePlan plan)
    {
        VoiceTarget WindowTarget() =>
            plan.WindowTargetMode == WindowTargetMode.Named && plan.WindowCandidateId != null
                ? new CandidateWindowTarget(plan.WindowCandidateId)
                : new CurrentWindowTarget();

        return plan.Action switch
        {
            VoiceAction.OpenApp when plan.AppCandidateId != null =>
                new OpenAppStep(stepId, new AppTarget(plan.AppCandidateId), plan.ActivationMode),

            VoiceAction.FocusWindow when
                plan.WindowTargetMode == WindowTargetMode.Named && plan.WindowCandidateId != null =>
                new FocusWindowStep(stepId, new CandidateWindowTarget(plan.WindowCandidateId)),

            VoiceAction.CloseCurrentWindow =>
                new CloseWindowStep(stepId, WindowTarget()),

            VoiceAction.MaximizeCurrentWindow =>
                new MaximizeWindowStep(stepId, WindowTarget()),

            VoiceAction.MinimizeCurrentWindow =>
                new MinimizeWindowStep(stepId, WindowTarget()),

            VoiceAction.SnapCurrentWindow when plan.Snap.HasValue =>
                new SnapWindowStep(stepId, WindowTarget(), plan.Snap.Value),

            VoiceAction.MediaControl when plan.Media.HasValue =>
                new MediaControlStep(stepId, plan.Media.Value),

            VoiceAction.SetVolume when plan.VolumeValue.HasValue =>
                new SetVolumeStep(stepId, plan.VolumeValue.Value),

            VoiceAction.AdjustVolume when plan.VolumeAdjust.HasValue =>
                new AdjustVolumeStep(stepId, plan.VolumeAdjust.Value),

            _ => null
        };
    }
}
