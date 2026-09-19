namespace VoiceOS.Core.Audio;

public record RecordingMetadata(
    DateTimeOffset ActivationPressTime,
    DateTimeOffset RecordingStart,
    DateTimeOffset MicCaptureStarted,
    DateTimeOffset StopActivationTime,
    DateTimeOffset CaptureStopped,
    DateTimeOffset WavFinalized,
    double TotalDurationSeconds);
