namespace VoiceOS.Core.Activation;

/// <summary>The bounded interaction path selected by the physical activation control.</summary>
public enum InteractionKind
{
    Command,
    Dictation
}

/// <summary>Lifecycle event retained for existing shell consumers.</summary>
public sealed record InteractionStateChanged(InteractionKind Kind, ActivationState State);
