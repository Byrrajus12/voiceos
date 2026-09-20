using VoiceOS.Core.Audio;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Speech;

namespace VoiceOS.Core.Activation;

public record PipelineResult(
    RecordingMetadata Recording,
    TranscriptionResult? Transcription,
    DecisionResult? Decision,
    VoicePlan? Plan,
    DateTimeOffset PipelineStart,
    DateTimeOffset? SttStart,
    DateTimeOffset? SttEnd,
    DateTimeOffset? JevStart,
    DateTimeOffset? JevEnd,
    DateTimeOffset PipelineEnd);
