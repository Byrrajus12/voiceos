namespace VoiceOS.Core.Interaction;

/// <summary>
/// Minimal surface-neutral observe/decide/execute loop. It accepts only actions
/// advertised by the current observation, records structured outcomes, and
/// stops when evidence is not changing instead of retrying blindly.
/// </summary>
/// <param name="activeFamilies">Families whose verdicts may end the run when <paramref name="proofMode"/> is On;
/// null means every family. Verdicts of other families are still evaluated and recorded (shadow behavior).</param>
public sealed class InteractionEngine(IProofEvaluator? proof = null, ProofMode proofMode = ProofMode.Off,
    IReadOnlySet<ProofFamily>? activeFamilies = null)
{
    public async ValueTask<InteractionRunResult> RunAsync(
        InteractionGoal goal,
        IInteractionSurface surface,
        IInteractionDecisionSource decisions,
        InteractionBudget? budget = null,
        CancellationToken cancellationToken = default,
        InteractionObservation? initialObservation = null)
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
        var ledger = new EffectLedger(goal.Step?.Id ?? "s1");
        var rejectedCompletionStates = new HashSet<string>(StringComparer.Ordinal);
        var progress = new InteractionProgress(0, 0, 0, 0);
        double previousGoalConfidence = 0;
        bool awaitingProgressJudgment = false;
        // A later plan step continues from the state the previous one ended in; no second observation.
        var observation = initialObservation ?? await surface.ObserveAsync(token).ConfigureAwait(false);
        if (initialObservation is null) ledger.Append(observation.Effects);
        var proofs = new List<ProofRecord>();
        if (goal.Step is { } initialStep && Evaluate(initialStep, null, null) is { Status: ProofStatus.Proved } initialProof
            && Acts(initialProof))
            return Finish(InteractionCompletionState.Complete, $"proof:{initialProof.Rule}");

        while (progress.Decisions < budget.MaxDecisions && progress.Actions < budget.MaxActions)
        {
            token.ThrowIfCancellationRequested();
            var context = new InteractionDecisionContext(goal, observation, history.ToArray(), budget, progress, ledger.All.ToArray());
            var decision = await decisions.DecideAsync(context, token).ConfigureAwait(false);
            progress = progress with { Decisions = progress.Decisions + 1 };
            if (awaitingProgressJudgment)
            {
                var last = history.Last();
                var sameActionRepeated = history.TakeLast(3).Count(x =>
                    x.Action.Signature == last.Action.Signature) > 1;
                var advanced = decision.GoalConfidence > previousGoalConfidence + .08
                    // Scrolling that reveals new content is exploration, not a repeated no-op; the decision source bounds it.
                    || (HasRelevantStateChange(last) && (!sameActionRepeated || last.Action.Kind == InteractionActionKind.Scroll));
                progress = progress with { ConsecutiveNoProgress = advanced
                    ? 0 : progress.ConsecutiveNoProgress + 1 };
                awaitingProgressJudgment = false;
                if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                    return Finish(InteractionCompletionState.Incomplete,
                        "The browser task stopped after consecutive actions without semantic progress.", reasonCode: "no_progress");
            }

            if (decision.Completion == InteractionCompletionState.Uncertain)
                return Finish(InteractionCompletionState.Uncertain, decision.Detail ?? "Completion is uncertain.", decision.Choices,
                    decision.ReasonCode);

            if (decision.Completion == InteractionCompletionState.Complete)
            {
                var fresh = await surface.ObserveAsync(token).ConfigureAwait(false);
                ledger.Append(fresh.Effects);
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
                        "Completion was proposed again after rejection with unchanged evidence.", CompletionChoices(),
                        "completion_unconfirmed");
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
                    return Finish(InteractionCompletionState.Incomplete, "Completion was rejected without new evidence.",
                        reasonCode: "completion_unconfirmed");
                continue;
            }

            if (decision.Action is null)
                return Finish(InteractionCompletionState.Incomplete,
                    decision.Detail ?? "No bounded action was selected.", reasonCode: "no_action");

            var action = decision.Action;
            previousGoalConfidence = decision.GoalConfidence;
            if (WasFailedWithoutStateChange(action, observation.StateKey))
            {
                AddHistory(action,
                    InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                        "Repeated action failure was suppressed because the evidence is unchanged."),
                    observation.StateKey, suppressed: true, observation.Evidence);
                return Finish(InteractionCompletionState.Incomplete,
                    "A repeated failure was suppressed until the observed state changes.", reasonCode: "repeated_failure");
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
                catch (Exception ex) when (ex is not OperationCanceledException and not InfrastructureUnavailableException)
                {
                    result = InteractionActionResult.Fail(InteractionResultStatus.PlatformFailure, ex.Message);
                }
            }

            progress = progress with { Actions = progress.Actions + 1 };
            ledger.Append(result.Effects, action.Id);
            if (result.Status == InteractionResultStatus.TopologyAmbiguous)
            {
                AddHistory(action, result, observation.StateKey, suppressed: false,
                    observation.Evidence);
                return Finish(InteractionCompletionState.Uncertain,
                    result.Detail ?? "The browser action changed tabs ambiguously.", reasonCode: "topology");
            }
            // The companion definitively reported there is no earlier page: that is a failed operation, not a question.
            if (proofMode == ProofMode.On && action.Kind == InteractionActionKind.GoBack
                && (result.Effects ?? []).Any(static e => e.Kind == EffectKind.NoEffect && e.Get("reason") == "NO_HISTORY"))
            {
                AddHistory(action, result, observation.StateKey, suppressed: false, observation.Evidence);
                return Finish(InteractionCompletionState.Incomplete, "There is no earlier page in this tab.", reasonCode: "no_history");
            }
            var next = await surface.ObserveAfterActionAsync(token).ConfigureAwait(false);
            ledger.Append(next.Effects, action.Id);
            var changed = !StringComparer.Ordinal.Equals(observation.StateKey, next.StateKey);
            if (result.Succeeded && !changed)
            {
                result = InteractionActionResult.Fail(InteractionResultStatus.NoEffect,
                    result.Detail ?? "The action reported success but observable state did not change.");
                ledger.Append([new Effect(EffectKind.NoEffect, EffectSource.EngineRule, EffectStrength.Derived,
                    Data: new Dictionary<string, string> { ["reason"] = "state_unchanged" })], action.Id);
            }

            AddHistory(action, result, next.StateKey, suppressed: false, next.Evidence);
            progress = progress with
            {
                StateChanges = progress.StateChanges + (changed ? 1 : 0),
                ConsecutiveNoProgress = changed ? progress.ConsecutiveNoProgress
                    : progress.ConsecutiveNoProgress + 1
            };
            observation = next;
            if (goal.Step is { } step && Evaluate(step, action, decision.TargetBinding) is { } verdict && Acts(verdict))
            {
                if (verdict.Status == ProofStatus.Proved)
                    return Finish(InteractionCompletionState.Complete, $"proof:{verdict.Rule}");
                if (verdict.Status == ProofStatus.Refuted)
                    return Finish(InteractionCompletionState.Incomplete, $"proof_refuted:{verdict.Rule}", reasonCode: "proof_refuted");
            }
            if (progress.ConsecutiveNoProgress >= budget.MaxConsecutiveNoProgress)
                return Finish(InteractionCompletionState.Incomplete,
                    "The interaction stopped after reaching the no-progress budget.", reasonCode: "no_progress");
            awaitingProgressJudgment = changed;
        }

        return Finish(InteractionCompletionState.Incomplete, "The interaction budget was exhausted.", reasonCode: "budget_exhausted");

        // Shadow and On both evaluate and record; only On, for an active family, lets a verdict end the run.
        // The postcondition of a plan step is its completion mechanism, so it acts whatever the proof mode.
        bool Acts(ProofVerdict verdict) => goal.Step?.WithinPlan == true
            || proofMode == ProofMode.On && (activeFamilies is null || activeFamilies.Contains(verdict.Family));

        ProofVerdict? Evaluate(OutcomeStep step, InteractionAction? action, TargetBinding? binding)
        {
            if (proof is null || proofMode == ProofMode.Off && !step.WithinPlan) return null;
            var verdict = proof.Evaluate(new(step, ledger.All, action, binding, observation));
            proofs.Add(new(progress.Actions, verdict, binding));
            return verdict;
        }

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
            IReadOnlyList<InteractionChoice>? choices = null,
            string? reasonCode = null)
            => new(state, observation, history.ToArray(), progress, detail, choices, ledger.All.ToArray(),
                proofs.Count == 0 ? null : proofs.ToArray(), reasonCode);

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
