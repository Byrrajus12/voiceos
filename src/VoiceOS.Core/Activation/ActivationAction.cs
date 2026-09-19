namespace VoiceOS.Core.Activation;

public abstract record ActivationAction
{
    public record StartRecording : ActivationAction;
    public record StopRecording : ActivationAction;
    public record StartGraceTimer(TimeSpan Duration) : ActivationAction;
    public record CancelGraceTimer : ActivationAction;
}
