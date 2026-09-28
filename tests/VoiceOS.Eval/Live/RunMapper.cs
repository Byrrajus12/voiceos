using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Eval.Live;

/// <summary>Maps a live ActivationRun plus before/after state probes into a plain, serializable TurnRecord.
/// Pure data transformation — no product or Windows calls happen here.</summary>
public static class RunMapper
{
    private static readonly HashSet<string> ModelStageNames = new(StringComparer.Ordinal)
        { "route", "direct_decision", "normalization", "browser_decision", "text_value", "completion_confirmation" };

    public static TurnRecord Map(int index, string transcript, ActivationRun run, StateSnapshot initial, StateSnapshot final)
    {
        var terminal = run.TerminalSnapshot;
        var outcomeClass = Classify(run, terminal);
        var stages = run.Trace?.Stages ?? [];
        var latency = new LatencyInfo(
            run.Trace?.ElapsedMs ?? 0, run.Trace?.FirstExternalActionMs,
            stages.Select(s => new LatencyStageInfo(s.Name, s.StartMs, s.ElapsedMs)).ToArray(),
            stages.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.ElapsedMs)),
            stages.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Count()));

        var counts = new CountInfo(
            run.ActionCount, run.BrowserOutcome?.Decisions ?? 0,
            run.ProgramResult?.StepResults.Count ?? 0,
            CountNewTabs(initial, final),
            stages.Count(s => ModelStageNames.Contains(s.Name)),
            terminal?.Phase == ApplicationInteractionPhase.NeedsChoice ? 1 : 0);

        return new TurnRecord(index, transcript, run.ActivationId, run.PostSttStart ?? run.StartedAt, run.CompletedAt,
            run.Lane, run.Outcome, outcomeClass, terminal?.Phase.ToString(), terminal?.Status, run.Failure?.Message,
            MapRoute(run.InitialRoute), MapRoute(run.Route), run.ReroutedFromDirect, MapScope(run.Scope),
            MapExecution(run), latency, counts, initial, final, [], Classification.HarnessError) { Heads = run.RouteHeads };
    }

    private static OutcomeClass Classify(ActivationRun run, ApplicationInteractionSnapshot? terminal)
    {
        if (run.Failure is not null) return OutcomeClass.Failed;
        if (terminal?.Phase == ApplicationInteractionPhase.NeedsChoice) return OutcomeClass.Clarify;
        if (run.Outcome == "Unavailable") return OutcomeClass.Unsupported;
        if (run.Outcome is "Succeeded" or "Complete" && terminal?.Phase == ApplicationInteractionPhase.Succeeded)
            return OutcomeClass.Complete;
        return OutcomeClass.Failed;
    }

    private static RouteInfo? MapRoute(CommandRouteDecision? route) => route is null ? null : new RouteInfo(
        route.Route, route.Confidence, route.Reason, route.Detail, route.MediaRequestKind, route.MediaOperation,
        route.DestinationKind, route.DestinationName, route.ExplicitUrl?.ToString(), route.TabDisposition,
        route.SurfacePreference, route.EndState, route.GoalShape, route.TaskRelation, route.ContextDependency,
        route.RequestedEntity, route.NamedTabTarget);

    private static ScopeInfo? MapScope(ExecutionScopeDecision? scope) => scope is null ? null : new ScopeInfo(
        scope.Kind, scope.Browser?.Kind, scope.Browser?.TabId, scope.Browser?.Destination?.ToString(),
        scope.Browser?.NamedServiceHint, scope.Browser?.FocusOnly ?? false, scope.Detail,
        scope.Native?.ProcessName, scope.Native?.AppCandidateId);

    private static ExecutionInfo MapExecution(ActivationRun run)
    {
        var plan = run.Decision?.Plan;
        var browser = run.BrowserOutcome is { } b ? new BrowserExecutionInfo(
            b.Completion.ToString(), b.Detail, b.Url, b.Title, b.Decisions, b.Actions, b.TabId, b.Choices?.Count ?? 0)
            : null;
        var activities = run.BrowserActivities.Select(a => new BrowserActivityInfo(
            a.Operation?.ToString(), a.TargetRole, a.TargetName, a.OpeningTab, a.Destination, a.Query)).ToArray();
        var steps = run.Program?.Steps.Select(s => StepName(s.GetType().Name)).ToArray() ?? [];
        var results = run.ProgramResult?.StepResults.Select(r =>
            new StepResultInfo(r.StepId, r.Status.ToString(), r.Message)).ToArray() ?? [];
        return new ExecutionInfo(plan?.Action.ToString(), plan?.AppCandidateId, plan?.WindowCandidateId,
            plan?.Confidence, run.DirectTargetVerdict?.ToString(), steps, results, browser, activities,
            AttemptedSteps(run.Program, results.Select(static r => r.StepId)));
    }

    /// <summary>Program steps the executor actually attempted (a result exists for their StepId), with
    /// the typed operands of media and volume steps. Planned-but-blocked steps are excluded.</summary>
    public static IReadOnlyList<AttemptedDirectStep> AttemptedSteps(VoiceProgram? program, IEnumerable<string> attemptedStepIds)
    {
        var attempted = attemptedStepIds.ToHashSet(StringComparer.Ordinal);
        return (program?.Steps ?? []).Where(s => attempted.Contains(s.StepId))
            .Select(static s => s switch
            {
                MediaControlStep m => new AttemptedDirectStep(m.StepId, "MediaControl", MediaOperation: m.Operation),
                SetVolumeStep v => new AttemptedDirectStep(v.StepId, "SetVolume", VolumeValue: v.Value),
                AdjustVolumeStep a => new AttemptedDirectStep(a.StepId, "AdjustVolume",
                    VolumeDirection: a.Direction, VolumeAmount: a.Amount),
                _ => new AttemptedDirectStep(s.StepId, StepName(s.GetType().Name))
            }).ToArray();
    }

    /// <summary>Tabs observed after the turn that did not exist before it. Only meaningful when the
    /// Companion was connected for both probes; otherwise 0.</summary>
    public static int CountNewTabs(StateSnapshot initial, StateSnapshot final)
    {
        if (!initial.BrowserConnected || !final.BrowserConnected) return 0;
        var before = initial.Tabs.Select(static t => t.TabId).ToHashSet();
        return final.Tabs.Count(t => !before.Contains(t.TabId));
    }

    /// <summary>VoiceStep type name without its "Step" suffix, matching directSteps expectations.</summary>
    private static string StepName(string typeName)
        => typeName.EndsWith("Step", StringComparison.Ordinal) ? typeName[..^4] : typeName;
}
