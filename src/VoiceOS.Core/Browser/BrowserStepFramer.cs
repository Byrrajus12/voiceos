using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Browser;

/// <summary>
/// Deterministically frames a normalized browser goal as one <see cref="OutcomeStep"/>. The family is
/// derived only from the normalizer's end state and grounded fields, never from the wording of the
/// request. Anything uncertain frames as <see cref="ProofFamily.Reach"/>, which preserves today's
/// behavior. In this pass the step is observational and changes no decision.
/// </summary>
internal static class BrowserStepFramer
{
    internal const string StepId = "s1";

    public static CommandPlan Frame(BrowserGoal goal, string turnId)
    {
        var normalized = goal.Normalization;
        var family = Family(normalized);
        var query = family == ProofFamily.Find ? GroundedQuery(normalized!) : null;
        var phrase = FirstUsable(normalized?.Descriptor, normalized?.Objective, goal.OriginalUtterance)!;
        return new CommandPlan(turnId, [new OutcomeStep(StepId, family, new Descriptor(phrase, query),
            DescriptorReliable: normalized?.UnresolvedReference != true)]);
    }

    internal static ProofFamily Family(BrowserGoalNormalization? normalized)
    {
        if (normalized is null) return ProofFamily.Reach;
        return normalized.EndState switch
        {
            SemanticEndState.SurfaceReady when string.IsNullOrWhiteSpace(normalized.ResourceType)
                || BrowserCompletionEvidence.IsSiteItself(normalized.ResourceType) => ProofFamily.Surface,
            SemanticEndState.ResultsVisible when GroundedQuery(normalized) is not null => ProofFamily.Find,
            SemanticEndState.ResourceOpened or SemanticEndState.ContentActive
                when FirstUsable(normalized.Descriptor) is not null => ProofFamily.Activate,
            _ => ProofFamily.Reach
        };
    }

    private static string? GroundedQuery(BrowserGoalNormalization normalized)
        => FirstUsable(normalized.SearchQueries.ToArray());

    private static string? FirstUsable(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
