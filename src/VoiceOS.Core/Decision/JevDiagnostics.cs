namespace VoiceOS.Core.Decision;

public static class JevDiagnostics
{
    public sealed record SummaryRecord(string Stage, string Head, string? Choice, double? P,
        string? RunnerUp, double? P2, double? Margin, double Confidence);

    public static SummaryRecord Record(string stage, string head, JevAnswer answer)
        => new(stage, head, answer.SelectedChoice, answer.Top?.P, answer.RunnerUp?.Choice,
            answer.RunnerUp?.P, answer.Margin, answer.Confidence);

    public static string Summarize(string head, JevAnswer answer)
        => $"head={head} selected={answer.SelectedChoice} top={answer.Top?.Choice} p={answer.Top?.P:F3} " +
            $"2nd={answer.RunnerUp?.Choice} p2={answer.RunnerUp?.P:F3} margin={answer.Margin:F3} conf={answer.Confidence:F3}" +
            (answer.SelectedIsArgMax ? "" : " WARNING selected_not_argmax");
}
