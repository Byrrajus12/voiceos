namespace VoiceOS.Core.Interaction;

/// <summary>
/// Minimal surface-neutral observe/decide/execute loop. It accepts only actions
/// advertised by the current observation, records structured outcomes, and
/// stops when evidence is not changing instead of retrying blindly.
/// </summary>
public sealed class InteractionEngine
{
    public async ValueTask<InteractionRunResult> RunAsync(
        InteractionGoal goal,
        IInteractionSurface surface,
        IInteractionDecisionSource decisions,
        InteractionBudget? budget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(decisions);
        if (!goal.IsValid)
            throw new ArgumentException("The interaction goal cannot be empty.", nameof(goal));

        budget ??= new InteractionBudget();
        budget.Validate();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget.EffectiveTimeLimit);
        var token = timeout.Token;

        var history = new List<InteractionHistoryEntry>();
        var rejectedCompletionStates = new HashSet<string>(StringComparer.Ordinal);
        var progress = new InteractionProgress(0, 0, 0, 0);
        var observation = await surface.ObserveAsync(token).ConfigureAwait(false);

        while (progress.Decisions < budget.MaxDecisions && progress.Actions < budget.MaxActions)
        {
            token.ThrowIfCancellationRequested();
            var context = new InteractionDecisionContext(goal, observation, history.ToArray(), budget, progress);
            var decision = await decisions.DecideAsync(context, token).ConfigureAwait(false);
            progress = progress with { Decisions = progress.Decisions + 1 };

            if (decision.Completion == InteractionCompletionState.Uncertain)
                return Finish(InteractionCompletionState.Uncertain, decision.Detail ?? "Completion is uncertain.");

            if (decision.Completion == InteractionCompletionState.Complete)
            {
                if (!rejectedCompletionStates.Add(observation.StateKey))
                {
                    AddHistory(InteractionAction.Done,
                        InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                            "Repeated completion was suppressed because the evidence is unchanged."),
                        observation.StateKey, suppressed: true);
                    return Finish(InteractionCompletionState.Uncertain,
                        "Completion was proposed again after rejection with unchanged evidence.");
                }

                var assessment = await surface.AssessCompletionAsync(goal, observation, token).ConfigureAwait(false);
                if (assessment.State == InteractionCompletionState.Complete)
                    return Finish(InteractionCompletionState.Complete, assessment.Detail);
                if (assessment.State == InteractionCompletionState.Uncertain)
                    return Finish(InteractionCompletionState.Uncertain, assessment.Detail);

                AddHistory(InteractionAction.Done,
                    InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                        assessment.Detail ?? "Completion was rejected by current evidence."),
                    observation.StateKey, suppressed: false);
                progress = progress with { ConsecutiveNoProgress = progress.ConsecutiveNoProgress + 1 };
                if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                    return Finish(InteractionCompletionState.Incomplete, "Completion was rejected without new evidence.");
                continue;
            }

            if (decision.Action is null)
                return Finish(InteractionCompletionState.Incomplete,
                    decision.Detail ?? "No bounded action was selected.");

            var action = decision.Action;
            if (WasFailedWithoutStateChange(action, observation.StateKey))
            {
                AddHistory(action,
                    InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                        "Repeated action failure was suppressed because the evidence is unchanged."),
                    observation.StateKey, suppressed: true);
                return Finish(InteractionCompletionState.Incomplete,
                    "A repeated failure was suppressed until the observed state changes.");
            }

            InteractionActionResult result;
            if (!IsAdvertised(action, observation))
            {
                result = InteractionActionResult.Fail(InteractionResultStatus.ScopeViolation,
                    "The action was not advertised by the current observation.");
            }
            else
            {
                try
                {
                    result = await surface.ExecuteAsync(action, observation, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = InteractionActionResult.Fail(InteractionResultStatus.PlatformFailure, ex.Message);
                }
            }

            progress = progress with { Actions = progress.Actions + 1 };
            var next = await surface.ObserveAsync(token).ConfigureAwait(false);
            var changed = !StringComparer.Ordinal.Equals(observation.StateKey, next.StateKey);
            if (result.Succeeded && !changed)
                result = InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                    result.Detail ?? "The action reported success but observable state did not change.");

            AddHistory(action, result, next.StateKey, suppressed: false);
            progress = progress with
            {
                StateChanges = progress.StateChanges + (changed ? 1 : 0),
                ConsecutiveNoProgress = changed ? 0 : progress.ConsecutiveNoProgress + 1
            };
            observation = next;

            if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                return Finish(InteractionCompletionState.Incomplete,
                    "The interaction stopped after reaching the no-progress budget.");
        }

        return Finish(InteractionCompletionState.Incomplete, "The interaction budget was exhausted.");

        bool WasFailedWithoutStateChange(InteractionAction action, string stateKey) => history.Any(entry =>
            !entry.Suppressed
            && entry.Result.IsFailure
            && entry.ObservationStateKey == stateKey
            && entry.ResultingStateKey == stateKey
            && entry.Action.Signature == action.Signature);

        void AddHistory(
            InteractionAction action,
            InteractionActionResult result,
            string resultingStateKey,
            bool suppressed)
        {
            history.Add(new(observation.StateKey, action, result, resultingStateKey, suppressed, DateTimeOffset.UtcNow));
            if (history.Count > budget.MaxHistory)
                history.RemoveAt(0);
        }

        InteractionRunResult Finish(InteractionCompletionState state, string? detail)
            => new(state, observation, history.ToArray(), progress, detail);
    }

    private static bool IsAdvertised(InteractionAction action, InteractionObservation observation)
        => action.Kind != InteractionActionKind.Complete
            && observation.Candidates.SelectMany(static candidate => candidate.Actions).Contains(action);
}
