namespace VoiceOS.Core.Activation;

public record MachineContext(
    ActivationState State,
    bool IsToggled = false,
    DateTimeOffset? PressedAt = null)
{
    public static MachineContext Initial => new(ActivationState.Idle);
}
