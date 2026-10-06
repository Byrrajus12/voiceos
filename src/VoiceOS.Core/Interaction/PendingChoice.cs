namespace VoiceOS.Core.Interaction;

/// <summary>What selecting an option does to the suspended step.</summary>
public enum ChoiceResolution
{
    /// <summary>The selected control IS what the step was looking for: it is activated as the step's bound target.</summary>
    SatisfiesStep,
    /// <summary>The selected control only clears what stood in the way: it is activated once, then the SAME step runs again.</summary>
    ClearsBlocker
}

/// <summary>
/// One real, grounded option of a <see cref="PendingChoice"/>. <see cref="DisplayText"/> and <see cref="SecondaryText"/>
/// are the site's/app's own words (accessible name, value, nearby text), never generated. <see cref="TargetRef"/> is the
/// candidate's reference in the observation the choice was built from; <see cref="InternalConfidence"/> is kept for
/// diagnostics and is never shown. <see cref="Href"/>, <see cref="Role"/>, <see cref="Context"/> and <see cref="Position"/> are the
/// identity the option is found again by once references have gone stale; they are never displayed. <see cref="AlternateLabels"/> are
/// the names of the same result's other handles (thumbnail, duration-adjacent node).
/// </summary>
public sealed record ChoiceOption(string ChoiceId, string TargetRef, string DisplayText, string? SecondaryText = null,
    double? InternalConfidence = null, string? Href = null, string? Role = null, string? Context = null, int? Position = null,
    IReadOnlyList<string>? AlternateLabels = null);

/// <summary>
/// A step the runtime suspended because several real, grounded options each satisfy the user's intent and only the user
/// can say which. The suspended execution owns this object: answering it (by a click today, by speech later, see
/// <see cref="PendingChoiceMatcher"/>) resumes that same step, it never starts a task.
/// </summary>
/// <param name="ExecutionId">The run (activation/session) that is suspended.</param>
/// <param name="StepId">The plan step that is suspended (<c>s2</c>).</param>
/// <param name="Reason">One short question shown above the options.</param>
/// <param name="ObservationRevision">The observation <see cref="ChoiceOption.TargetRef"/> values belong to.</param>
public sealed record PendingChoice(string ExecutionId, string StepId, string Reason, IReadOnlyList<ChoiceOption> Options,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, ChoiceResolution Resolution = ChoiceResolution.SatisfiesStep,
    long ObservationRevision = 0, int? TabId = null)
{
    public const int MaxOptions = 4;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>The option with this id, only if it belongs to this choice.</summary>
    public ChoiceOption? Find(string? choiceId)
        => choiceId is null ? null : Options.FirstOrDefault(o => StringComparer.Ordinal.Equals(o.ChoiceId, choiceId));
}

/// <summary>
/// Deterministic seam for answering a <see cref="PendingChoice"/> in words: "the Adele one", "second", "over 18".
/// Not wired to the front door yet. The intended flow is
/// <c>if (pendingChoice exists) resolve the utterance against its options, else normal command front door</c>.
/// Only an unambiguous single match resolves; anything else returns null so the utterance is treated as a new command.
/// </summary>
public static class PendingChoiceMatcher
{
    private static readonly string[] Ordinals = ["first", "second", "third", "fourth"];
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "one", "that", "this", "please", "choose", "pick", "select", "click", "i", "am", "im", "want", "it", "is" };

    public static ChoiceOption? Match(string? utterance, PendingChoice choice)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return null;
        var spoken = Words(utterance);
        for (var i = 0; i < Ordinals.Length && i < choice.Options.Count; i++)
            if (spoken.Contains(Ordinals[i])) return choice.Options[i];
        var scored = choice.Options.Select(o => (Option: o, Hits: Words($"{o.DisplayText} {o.SecondaryText}")
                .Count(w => spoken.Contains(w) && !Filler.Contains(w))))
            .Where(static x => x.Hits > 0).OrderByDescending(static x => x.Hits).ToArray();
        return scored.Length == 1 || scored.Length > 1 && scored[0].Hits > scored[1].Hits ? scored[0].Option : null;
    }

    private static HashSet<string> Words(string text)
        => System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(static m => m.Value).ToHashSet();
}
