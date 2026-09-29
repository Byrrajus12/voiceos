using Microsoft.Extensions.Logging;

namespace VoiceOS.Core.Decision;

public static class JevDiagnostics
{
    public static void Log(ILogger? logger, string stage, string head, JevAnswer answer)
    {
        Activation.LatencyTrace.Current?.RecordHead(Record(stage switch {
            "Command route head" => "route", "Direct head" => "direct", "Contextual surface" => "scope_surface",
            "Named tab" => "scope_named_tab", "Installed app" => "scope_app", "Browser heads" => "browser", _ => stage
        }, head, answer));
        if (answer.SelectedIsArgMax)
            logger?.LogInformation("{Stage} {Summary}", stage, Summarize(head, answer));
        else
            logger?.LogWarning("{Stage} {Summary}", stage, Summarize(head, answer));
    }
    public sealed record SummaryRecord(string Stage, string Head, string? Choice, double? P,
        string? RunnerUp, double? P2, double? Margin, double Confidence)
    {
        public string? TopChoice { get; init; }
        public double? SelectedP { get; init; }
    }

    public static SummaryRecord Record(string stage, string head, JevAnswer answer)
        => new(stage, head, answer.SelectedChoice, answer.Top?.P, answer.RunnerUp?.Choice,
            answer.RunnerUp?.P, answer.Margin, answer.Confidence) {
                TopChoice = answer.Top?.Choice, SelectedP = answer.SelectedChoice is { } choice
                    && answer.Probabilities.TryGetValue(choice, out var p) ? p : null };

    public static string Summarize(string head, JevAnswer answer)
        => $"head={head} selected={answer.SelectedChoice} top={answer.Top?.Choice} p={answer.Top?.P:F3} " +
            $"2nd={answer.RunnerUp?.Choice} p2={answer.RunnerUp?.P:F3} margin={answer.Margin:F3} conf={answer.Confidence:F3}" +
            (answer.SelectedIsArgMax ? "" : " WARNING selected_not_argmax");
}
