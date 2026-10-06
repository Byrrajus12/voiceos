namespace VoiceOS.Core.Activation;

public enum ProductUiPhase { Idle, Listening, Understanding, Acting, Success, Clarify, Error }

/// <param name="Choice">Clarify only: the real options the suspended execution is waiting on (clickable in the UI).</param>
public sealed record ProductUiLifecycle(ProductUiPhase Phase, string? Message = null,
    long Generation = 0, Interaction.PendingChoice? Choice = null);
