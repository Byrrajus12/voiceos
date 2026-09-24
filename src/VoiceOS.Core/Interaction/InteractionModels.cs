namespace VoiceOS.Core.Interaction;

public sealed record InteractionGoal(string Text)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Text);
}

public enum InteractionActionKind
{
    Activate,
    Focus,
    SetText,
    TypeText,
    Select,
    Scroll,
    PressKey,
    GoBack,
    Wait,
    Complete
}

/// <summary>An action chosen from the bounded actions advertised by an observation.</summary>
public sealed record InteractionAction(
    string Id,
    InteractionActionKind Kind,
    string? TargetId = null,
    string? Text = null,
    string? Direction = null)
{
    public string Signature => string.Join('|', Kind, TargetId ?? "", Text ?? "", Direction ?? "");

    public static InteractionAction Done { get; } = new("complete", InteractionActionKind.Complete);
}

public sealed record InteractionCandidate(
    string Id,
    string Label,
    IReadOnlyList<InteractionAction> Actions);

/// <summary>
/// Surface-neutral evidence. StateKey must change whenever evidence relevant to
/// action safety or goal completion changes.
/// </summary>
public sealed record InteractionObservation(
    long Revision,
    string StateKey,
    string Evidence,
    IReadOnlyList<InteractionCandidate> Candidates);

public enum InteractionResultStatus
{
    Success,
    StaleTarget,
    OccludedTarget,
    TargetUnavailable,
    NoEffect,
    FocusMismatch,
    UnsupportedAction,
    ScopeViolation,
    PlatformFailure
}

public sealed record InteractionActionResult(InteractionResultStatus Status, string? Detail = null)
{
    public bool Succeeded => Status == InteractionResultStatus.Success;
    public bool IsFailure => !Succeeded;

    public static InteractionActionResult Ok(string? detail = null) => new(InteractionResultStatus.Success, detail);
    public static InteractionActionResult Fail(InteractionResultStatus status, string detail) => new(status, detail);
}

public enum InteractionCompletionState
{
    Complete,
    Incomplete,
    Uncertain
}

public sealed record InteractionCompletionAssessment(
    InteractionCompletionState State,
    string? Detail = null,
    IReadOnlyList<InteractionChoice>? Choices = null);

public sealed record InteractionChoice(string Id, string Label, string? Detail = null);

public sealed record InteractionDecision(
    InteractionCompletionState Completion,
    InteractionAction? Action = null,
    string? Detail = null,
    IReadOnlyList<InteractionChoice>? Choices = null)
{
    public static InteractionDecision Act(InteractionAction action, string? detail = null)
        => new(InteractionCompletionState.Incomplete, action, detail);
    public static InteractionDecision Done(string? detail = null)
        => new(InteractionCompletionState.Complete, Detail: detail);
    public static InteractionDecision Unsure(string? detail = null, IReadOnlyList<InteractionChoice>? choices = null)
        => new(InteractionCompletionState.Uncertain, Detail: detail, Choices: choices);
}

public sealed record InteractionHistoryEntry(
    string ObservationStateKey,
    InteractionAction Action,
    InteractionActionResult Result,
    string ResultingStateKey,
    bool Suppressed,
    DateTimeOffset Timestamp,
    string? ObservationEvidence = null,
    string? ResultingEvidence = null,
    string? TargetLabel = null);

public sealed record InteractionBudget(
    int MaxDecisions = 12,
    int MaxActions = 8,
    int MaxConsecutiveNoProgress = 2,
    int MaxHistory = 12,
    TimeSpan? TimeLimit = null)
{
    public TimeSpan EffectiveTimeLimit => TimeLimit ?? TimeSpan.FromSeconds(20);

    internal void Validate()
    {
        if (MaxDecisions <= 0 || MaxActions <= 0 || MaxConsecutiveNoProgress <= 0 || MaxHistory <= 0)
            throw new ArgumentOutOfRangeException(nameof(InteractionBudget), "All interaction budget limits must be positive.");
        if (EffectiveTimeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeLimit), "The interaction time limit must be positive.");
    }
}

public sealed record InteractionProgress(
    int Decisions,
    int Actions,
    int StateChanges,
    int ConsecutiveNoProgress);

public sealed record InteractionDecisionContext(
    InteractionGoal Goal,
    InteractionObservation Observation,
    IReadOnlyList<InteractionHistoryEntry> RecentHistory,
    InteractionBudget Budget,
    InteractionProgress Progress);

public sealed record InteractionRunResult(
    InteractionCompletionState Completion,
    InteractionObservation Observation,
    IReadOnlyList<InteractionHistoryEntry> RecentHistory,
    InteractionProgress Progress,
    string? Detail = null,
    IReadOnlyList<InteractionChoice>? Choices = null);

public interface IInteractionSurface
{
    ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default);

    ValueTask<InteractionActionResult> ExecuteAsync(
        InteractionAction action,
        InteractionObservation observation,
        CancellationToken cancellationToken = default);

    ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(
        InteractionGoal goal,
        InteractionObservation observation,
        IReadOnlyList<InteractionHistoryEntry> recentHistory,
        CancellationToken cancellationToken = default);
}

public interface IInteractionDecisionSource
{
    ValueTask<InteractionDecision> DecideAsync(
        InteractionDecisionContext context,
        CancellationToken cancellationToken = default);
}
