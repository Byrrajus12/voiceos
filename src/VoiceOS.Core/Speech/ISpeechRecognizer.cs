namespace VoiceOS.Core.Speech;

public interface ISpeechRecognizer : IDisposable
{
    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct = default);
    bool IsReady { get; }
}
