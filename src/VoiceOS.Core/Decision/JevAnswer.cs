namespace VoiceOS.Core.Decision;

public record JevAnswer(
    string QuestionType,
    string? SelectedChoice,
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence);
