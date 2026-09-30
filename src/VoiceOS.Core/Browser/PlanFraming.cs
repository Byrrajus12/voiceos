using System.Text.RegularExpressions;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>Maps a semantic plan step to the outcome step whose postcondition completes it.</summary>
internal static class PlanFraming
{
    private static readonly Regex PronounOnly = new(@"^\s*(it|that|this|them|those|these|that one|this one|the one|one)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static OutcomeStep Frame(InteractionPlan plan, BrowserGoal goal, BrowserExecutionScope? scope, string turnId)
    {
        var step = plan.Current;
        var id = $"s{plan.CurrentStepIndex + 1}";
        var target = FirstUsable(step.Target, step.Description)!;
        // A described target that is only a pronoun needs context the step does not carry; it cannot be proved locally.
        var referential = step.Kind is PlanStepKind.Open or PlanStepKind.Locate && PronounOnly.IsMatch(step.Target ?? "")
            && scope is not { ReferentResolved: true };
        return step.Kind switch
        {
            PlanStepKind.Reach => new(id, ProofFamily.Surface, new(target, Utterance: goal.OriginalUtterance), WithinPlan: true),
            PlanStepKind.Search when !string.IsNullOrWhiteSpace(step.Query)
                => new(id, ProofFamily.Find, new(step.Description, step.Query, goal.OriginalUtterance), WithinPlan: true),
            PlanStepKind.Locate when !referential
                => new(id, ProofFamily.Locate, new(target, Utterance: goal.OriginalUtterance), WithinPlan: true),
            PlanStepKind.Open when !referential
                => new(id, ProofFamily.Activate, new(target, Utterance: goal.OriginalUtterance), WithinPlan: true),
            _ => new(id, ProofFamily.Reach, new(step.Description, Utterance: goal.OriginalUtterance), WithinPlan: true)
        };
    }

    private static string? FirstUsable(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
