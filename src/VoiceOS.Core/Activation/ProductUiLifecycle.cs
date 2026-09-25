namespace VoiceOS.Core.Activation;

public enum ProductUiPhase { Idle, Listening, Understanding, Acting, Success, Clarify, Error }

public sealed record ProductUiLifecycle(ProductUiPhase Phase, string? Message = null,
    long Generation = 0);
