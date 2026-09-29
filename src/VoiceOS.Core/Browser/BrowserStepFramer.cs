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

    public static CommandPlan Frame(BrowserGoal goal, string turnId, BrowserExecutionScope? scope = null)
    {
        var normalized = goal.Normalization;
        var family = Family(normalized);
        var query = family == ProofFamily.Find ? GroundedQuery(normalized!) : null;
        // The normalized Objective is the open descriptor; the utterance stays as supporting context only.
        var phrase = FirstUsable(normalized?.Objective, goal.OriginalUtterance)!;
        return new CommandPlan(turnId, [new OutcomeStep(StepId, family, new Descriptor(phrase, query, goal.OriginalUtterance),
            DescriptorReliable: DescriptorReliable(family, scope))]);
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
                when FirstUsable(normalized.Objective) is not null => ProofFamily.Activate,
            _ => ProofFamily.Reach
        };
    }

    /// <summary>
    /// Whether the described target is anchored in this request. The normalizer sees one utterance and cannot
    /// tell "its docs" from "the docs"; the router already classified where a referenced target comes from
    /// and whether the request continues earlier work. Only a request the router established as a fresh task
    /// whose referent is either self-contained or supplied by the visible page is trusted; anything else
    /// (unknown, continuation, no router signal) caps proof so the legacy path decides.
    /// </summary>
    private static bool DescriptorReliable(ProofFamily family, BrowserExecutionScope? scope)
        => family is not (ProofFamily.Activate or ProofFamily.Find)
            || scope is { TaskRelationEstablished: true, TaskRelation: TaskRelation.NewTask,
                ContextDependency: ContextDependency.SelfContained or ContextDependency.RequiresCurrentSurface };

    private static string? GroundedQuery(BrowserGoalNormalization normalized)
        => FirstUsable(normalized.SearchQueries.ToArray());

    private static string? FirstUsable(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
