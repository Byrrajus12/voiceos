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
    Reach,
    /// <summary>A described target is present in the current content (plan steps only).</summary>
    Locate
}

/// <summary>
/// Open text describing what the user wants (the normalized objective); <see cref="Query"/> is a grounded
/// search string (Find only); <see cref="Utterance"/> is the original wording kept as supporting context.
/// </summary>
public sealed record Descriptor(string Phrase, string? Query = null, string? Utterance = null, string? SearchScope = null);

/// <summary>
/// One proof-oriented unit of a command. <see cref="DescriptorReliable"/> is false when the request
/// depends on earlier context the step does not represent.
/// </summary>
/// <param name="WithinPlan">The step belongs to a compiled <see cref="InteractionPlan"/>: its postcondition is local and
/// deterministic, evaluated whatever the proof mode, and is the completion mechanism of the step.</param>
public sealed record OutcomeStep(string Id, ProofFamily Family, Descriptor What, bool DescriptorReliable = true,
    bool WithinPlan = false);

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
