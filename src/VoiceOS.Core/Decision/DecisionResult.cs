namespace VoiceOS.Core.Decision;

public record DecisionResult(
    VoicePlan Plan,
    IReadOnlyDictionary<string, JevAnswer> RawAnswers,
    double RequestDurationMs,
    int InputTokens,
    int OutputTokens);
