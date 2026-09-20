namespace VoiceOS.Core.Speech;

public record TranscriptionResult(
    string Transcript,
    double AudioDurationMs,
    double TranscriptionDurationMs,
    string RecognizerName,
    bool Success,
    string? Error = null);
