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
        double previousGoalConfidence = 0;
        bool awaitingProgressJudgment = false;
        var observation = await surface.ObserveAsync(token).ConfigureAwait(false);

        while (progress.Decisions < budget.MaxDecisions && progress.Actions < budget.MaxActions)
        {
            token.ThrowIfCancellationRequested();
            var context = new InteractionDecisionContext(goal, observation, history.ToArray(), budget, progress);
            var decision = await decisions.DecideAsync(context, token).ConfigureAwait(false);
            progress = progress with { Decisions = progress.Decisions + 1 };
            if (awaitingProgressJudgment)
            {
                var last = history.Last();
                var sameActionRepeated = history.TakeLast(3).Count(x =>
                    x.Action.Signature == last.Action.Signature) > 1;
                var advanced = decision.GoalConfidence > previousGoalConfidence + .08
                    || (!sameActionRepeated && HasRelevantStateChange(last));
                progress = progress with { ConsecutiveNoProgress = advanced
                    ? 0 : progress.ConsecutiveNoProgress + 1 };
                awaitingProgressJudgment = false;
                if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                    return Finish(InteractionCompletionState.Incomplete,
                        "The browser task stopped after consecutive actions without semantic progress.");
            }

            if (decision.Completion == InteractionCompletionState.Uncertain)
                return Finish(InteractionCompletionState.Uncertain, decision.Detail ?? "Completion is uncertain.", decision.Choices);

            if (decision.Completion == InteractionCompletionState.Complete)
            {
                var fresh = await surface.ObserveAsync(token).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(observation.StateKey, fresh.StateKey))
                {
                    progress = progress with
                    {
                        StateChanges = progress.StateChanges + 1,
                        ConsecutiveNoProgress = 0
                    };
                    observation = fresh;
                }

                if (!rejectedCompletionStates.Add(observation.StateKey))
                {
                    AddHistory(InteractionAction.Done,
                        InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                            "Repeated completion was suppressed because the evidence is unchanged."),
                        observation.StateKey, suppressed: true, observation.Evidence);
                    return Finish(InteractionCompletionState.Uncertain,
                        "Completion was proposed again after rejection with unchanged evidence.", CompletionChoices());
                }

                var assessment = await surface.AssessCompletionAsync(goal, observation, history.ToArray(), token).ConfigureAwait(false);
                if (assessment.State == InteractionCompletionState.Complete)
                    return Finish(InteractionCompletionState.Complete, assessment.Detail);
                if (assessment.State == InteractionCompletionState.Uncertain)
                    return Finish(InteractionCompletionState.Uncertain, assessment.Detail,
                        assessment.Choices ?? CompletionChoices());

                AddHistory(InteractionAction.Done,
                    InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                        assessment.Detail ?? "Completion was rejected by current evidence."),
                    observation.StateKey, suppressed: false, observation.Evidence);
                progress = progress with { ConsecutiveNoProgress = progress.ConsecutiveNoProgress + 1 };
                if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                    return Finish(InteractionCompletionState.Incomplete, "Completion was rejected without new evidence.");
                continue;
            }

            if (decision.Action is null)
                return Finish(InteractionCompletionState.Incomplete,
                    decision.Detail ?? "No bounded action was selected.");

            var action = decision.Action;
            previousGoalConfidence = decision.GoalConfidence;
            if (WasFailedWithoutStateChange(action, observation.StateKey))
            {
                AddHistory(action,
                    InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                        "Repeated action failure was suppressed because the evidence is unchanged."),
                    observation.StateKey, suppressed: true, observation.Evidence);
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
            if (result.Status == InteractionResultStatus.TopologyAmbiguous)
            {
                AddHistory(action, result, observation.StateKey, suppressed: false,
                    observation.Evidence);
                return Finish(InteractionCompletionState.Uncertain,
                    result.Detail ?? "The browser action changed tabs ambiguously.");
            }
            var next = await surface.ObserveAsync(token).ConfigureAwait(false);
            var changed = !StringComparer.Ordinal.Equals(observation.StateKey, next.StateKey);
            if (result.Succeeded && !changed)
                result = InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                    result.Detail ?? "The action reported success but observable state did not change.");

            AddHistory(action, result, next.StateKey, suppressed: false, next.Evidence);
            progress = progress with
            {
                StateChanges = progress.StateChanges + (changed ? 1 : 0),
                ConsecutiveNoProgress = changed ? progress.ConsecutiveNoProgress
                    : progress.ConsecutiveNoProgress + 1
            };
            observation = next;
            if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                return Finish(InteractionCompletionState.Incomplete,
                    "The interaction stopped after reaching the no-progress budget.");
            awaitingProgressJudgment = changed;
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
            bool suppressed,
            string resultingEvidence)
        {
            history.Add(new(observation.StateKey, action, result, resultingStateKey, suppressed,
                DateTimeOffset.UtcNow, observation.Evidence, resultingEvidence,
                observation.Candidates.FirstOrDefault(candidate => candidate.Id == action.TargetId)?.Label));
            if (history.Count > budget.MaxHistory)
                history.RemoveAt(0);
        }

        InteractionRunResult Finish(
            InteractionCompletionState state,
            string? detail,
            IReadOnlyList<InteractionChoice>? choices = null)
            => new(state, observation, history.ToArray(), progress, detail, choices);

        static IReadOnlyList<InteractionChoice> CompletionChoices() =>
        [
            new("complete", "Looks complete", "Accept the current page as the result."),
            new("continue", "Keep going", "Continue interacting from the current page."),
            new("cancel", "Cancel", "Stop without taking another browser action.")
        ];

        static bool HasRelevantStateChange(InteractionHistoryEntry entry)
        {
            if (!entry.Result.Succeeded || entry.ObservationStateKey == entry.ResultingStateKey)
                return false;
            try
            {
                using var before = System.Text.Json.JsonDocument.Parse(entry.ObservationEvidence!);
                using var after = System.Text.Json.JsonDocument.Parse(entry.ResultingEvidence!);
                var a = before.RootElement;
                var b = after.RootElement;
                return Changed("current_url") || Changed("current_title") || Changed("elements");
                bool Changed(string property) => a.TryGetProperty(property, out var left)
                    && b.TryGetProperty(property, out var right) && left.GetRawText() != right.GetRawText();
            }
            catch { return false; }
        }
    }

    private static bool IsAdvertised(InteractionAction action, InteractionObservation observation)
        => action.Kind != InteractionActionKind.Complete
            && observation.Candidates.SelectMany(static candidate => candidate.Actions).Any(offered =>
                offered.Id == action.Id
                && offered.Kind == action.Kind
                && offered.TargetId == action.TargetId
                && offered.Direction == action.Direction
                && (offered.Text == action.Text || offered.Text is null
                    && action.Kind is InteractionActionKind.TypeText or InteractionActionKind.SetText));
}
