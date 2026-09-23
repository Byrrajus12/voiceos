using VoiceOS.Core.Activation;

namespace VoiceOS.Core.Interaction;

/// <summary>Product-level lifecycle only; no browser, DOM, UIA, or planner types leak here.</summary>
public enum ApplicationInteractionPhase
{
    Listening,
    Transcribing,
    Routing,
    Observing,
    Deciding,
    Executing,
    NeedsChoice,
    Succeeded,
    Failed,
    Idle
}

public sealed record InteractionChoice(string Id, string Label, string? Detail = null);

public sealed record ApplicationInteractionSnapshot(
    ApplicationInteractionPhase Phase,
    InteractionKind? Kind = null,
    string? Transcript = null,
    string? Status = null,
    IReadOnlyList<InteractionChoice>? Choices = null,
    InteractionCompletionState? Completion = null,
    InteractionResultStatus? Failure = null)
{
    public static ApplicationInteractionSnapshot Idle { get; } = new(ApplicationInteractionPhase.Idle);
}

public sealed class ApplicationInteractionSnapshotEventArgs(ApplicationInteractionSnapshot snapshot) : EventArgs
{
    public ApplicationInteractionSnapshot Snapshot { get; } = snapshot;
}

public interface IApplicationInteractionStateSource
{
    event EventHandler<ApplicationInteractionSnapshotEventArgs>? InteractionSnapshotChanged;
}
