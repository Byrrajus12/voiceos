using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>Pass 3 x Pass 2: a resolved referent grounds the descriptor; an unresolved one never certifies it.</summary>
public sealed class ReferentProofInteractionTests
{
    private static BrowserGoal Goal(SemanticEndState state) => BrowserGoal.FromUtterance("open its pilot") with
    {
        Normalization = new("command module pilot", null, null, null, null, [], "hint", [], state)
    };

    private static BrowserExecutionScope Continuation(bool resolved) => new(BrowserScopeKind.ExistingNamedTab)
    {
        ContextDependency = ContextDependency.Uncertain, TaskRelation = TaskRelation.ContinueRecent,
        TaskRelationEstablished = true, ReferentResolved = resolved
    };

    [Fact]
    public void ResolvedReferent_MakesTheContinuationDescriptorReliable()
    {
        var step = BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t", Continuation(true)).Only;
        Assert.Equal(ProofFamily.Activate, step.Family);
        Assert.True(step.DescriptorReliable);
    }

    [Fact]
    public void UnresolvedContinuation_StillCapsProof()
    {
        var step = BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t", Continuation(false)).Only;
        Assert.False(step.DescriptorReliable);
    }

    [Fact]
    public void Resolution_DoesNotChangeTheFamily_OrWhatProofRequires()
    {
        var resolved = BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t", Continuation(true)).Only;
        var plain = BrowserStepFramer.Frame(Goal(SemanticEndState.ResourceOpened), "t", Continuation(false)).Only;
        Assert.Equal(plain.Family, resolved.Family);
        Assert.Equal(plain.What, resolved.What);
    }

    [Fact]
    public void ReferentResolved_IsCarriedThroughRouterSignalsAndScopeCopies()
    {
        var scope = Continuation(true).WithRouterSignals(new(CommandRoute.ComputerUse, 1));
        Assert.True(scope.ReferentResolved);
        Assert.True((scope with { GoalShape = GoalShape.SurfaceOnly }).ReferentResolved);
    }
}
