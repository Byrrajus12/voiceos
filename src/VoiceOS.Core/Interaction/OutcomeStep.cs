namespace VoiceOS.Core.Interaction;

/// <summary>
/// How a successful outcome could later be verified. It never encodes what the user asked for:
/// "the third result" and "the official docs link" are the same family.
/// </summary>
public enum ProofFamily
{
    /// <summary>The requested surface (site/tab) is acquired.</summary>
    Surface,
    /// <summary>A grounded query was entered and its results are shown.</summary>
    Find,
    /// <summary>A described target was activated and its consequence observed.</summary>
    Activate,
    /// <summary>Use the existing legacy browser behavior; no deterministic proof is attempted.</summary>
    Reach
}

/// <summary>Open text describing what the user wants; <see cref="Query"/> is a grounded search string (Find only).</summary>
public sealed record Descriptor(string Phrase, string? Query = null);

/// <summary>
/// One proof-oriented unit of a command. <see cref="DescriptorReliable"/> is false when the request
/// depends on earlier context the step does not represent.
/// </summary>
public sealed record OutcomeStep(string Id, ProofFamily Family, Descriptor What, bool DescriptorReliable = true);

/// <summary>The ordered steps of one command. Exactly one step is supported for now.</summary>
public sealed record CommandPlan
{
    public CommandPlan(string turnId, IReadOnlyList<OutcomeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count != 1)
            throw new ArgumentException("A command plan currently has exactly one step.", nameof(steps));
        TurnId = turnId;
        Steps = steps;
    }

    public string TurnId { get; }
    public IReadOnlyList<OutcomeStep> Steps { get; }
    public OutcomeStep Only => Steps[0];
}
