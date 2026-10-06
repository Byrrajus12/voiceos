namespace VoiceOS.Core.Decision;

public record JevAnswer(
    string QuestionType,
    string? SelectedChoice,
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence)
{
    public bool HasDistribution => QuestionType == "choice" ? Probabilities.Count > 1 : QuestionType == "noul";
    private IEnumerable<(string Choice, double P)> Ranked => QuestionType == "noul"
        ? new[] { ("true", Probabilities.GetValueOrDefault("noul")), ("false", 1 - Probabilities.GetValueOrDefault("noul")) }
            .OrderByDescending(x => x.Item2).ThenByDescending(x => x.Item1 == SelectedChoice)
        : Probabilities.Select(x => (Choice: x.Key, P: x.Value)).OrderByDescending(x => x.P)
            .ThenByDescending(x => x.Choice == SelectedChoice).ThenBy(x => x.Choice, StringComparer.Ordinal);
    public (string Choice, double P)? Top => HasDistribution ? Ranked.First() : null;
    public (string Choice, double P)? RunnerUp => HasDistribution ? Ranked.Skip(1).First() : null;
    public double? Margin => Top?.P - RunnerUp?.P;
    public bool SelectedIsArgMax => Top is not { } top || top.Choice == SelectedChoice;
}
