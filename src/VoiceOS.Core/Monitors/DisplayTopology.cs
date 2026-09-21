namespace VoiceOS.Core.Monitors;

/// <summary>Immutable snapshot of the active display topology captured at a point in time.</summary>
public sealed record DisplayTopology(IReadOnlyList<MonitorInfo> Monitors)
{
    public static readonly DisplayTopology Empty = new(Array.Empty<MonitorInfo>());
}
