using System.Diagnostics;
using System.Globalization;

namespace VoiceOS.Core.Activation;

/// <summary>One timed stage, positioned relative to the start of the post-STT pipeline.</summary>
public sealed record LatencyStage(string Name, double StartMs, double ElapsedMs);

/// <summary>
/// Structured post-STT stage timings for one activation. It flows ambiently with the
/// pipeline so deep browser stages can report without changing their signatures.
/// Recording is observational only and never affects control flow.
/// </summary>
public sealed class LatencyTrace
{
    private static readonly AsyncLocal<LatencyTrace?> Ambient = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly List<LatencyStage> _stages = [];
    private double? _firstExternalActionMs;

    public LatencyTrace(string activationId) => ActivationId = activationId;

    public string ActivationId { get; }
    public static LatencyTrace? Current => Ambient.Value;
    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;
    public double? FirstExternalActionMs { get { lock (_lock) return _firstExternalActionMs; } }

    public IReadOnlyList<LatencyStage> Stages
    {
        get { lock (_lock) return _stages.ToArray(); }
    }

    /// <summary>Makes this trace current for the calling async flow and its callees.</summary>
    public static LatencyTrace Begin(string activationId)
    {
        var trace = new LatencyTrace(activationId);
        Ambient.Value = trace;
        return trace;
    }

    /// <summary>Records a stage that has just finished after <paramref name="elapsedMs"/>.</summary>
    public void Record(string stage, double elapsedMs)
    {
        var end = ElapsedMs;
        lock (_lock) _stages.Add(new(stage, Math.Max(0, end - elapsedMs), elapsedMs));
    }

    /// <summary>Marks the first dispatch of an externally visible side effect.</summary>
    public void MarkExternalAction()
    {
        var now = ElapsedMs;
        lock (_lock) _firstExternalActionMs ??= now;
    }

    /// <summary>Stable key=value summary; repeated stages are listed in order.</summary>
    public string Format()
    {
        static string Ms(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
        var stages = Stages;
        var parts = stages.GroupBy(static stage => stage.Name)
            .Select(group => group.Count() == 1
                ? $"{group.Key}_ms={Ms(group.First().ElapsedMs)}"
                : $"{group.Key}_ms=[{string.Join(",", group.Select(stage => Ms(stage.ElapsedMs)))}]")
            .ToList();
        if (FirstExternalActionMs is { } first) parts.Add($"first_action_ms={Ms(first)}");
        parts.Add($"total_post_stt_ms={Ms(ElapsedMs)}");
        return string.Join(" ", parts);
    }
}
