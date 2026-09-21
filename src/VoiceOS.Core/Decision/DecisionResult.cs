using VoiceOS.Core.Execution;

namespace VoiceOS.Core.Decision;

public record DecisionResult(
    VoicePlan Plan,
    VoiceProgram? Program,
    IReadOnlyDictionary<string, JevAnswer> RawAnswers,
    double RequestDurationMs,
    int InputTokens,
    int OutputTokens);
