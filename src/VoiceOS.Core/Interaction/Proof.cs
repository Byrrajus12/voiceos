namespace VoiceOS.Core.Interaction;

/// <summary>Off: proof is never evaluated. Shadow: evaluated and logged, never acted on. On: a verdict may end the run.</summary>
public enum ProofMode { Off, Shadow, On }

public enum ProofStatus
{
    /// <summary>Every obligation is satisfied by recorded effects: the step's outcome is established.</summary>
    Proved,
    /// <summary>The obligations are not yet applicable (no outcome-bearing action yet, or the last action was a means action).</summary>
    NotYet,
    /// <summary>An action ran with a consequence, but the evidence is not strong enough to establish the outcome.</summary>
    Inconclusive,
    /// <summary>A deterministic contradiction: what happened cannot be the requested outcome.</summary>
    Refuted
}

public sealed record ProofVerdict(
    ProofFamily Family, ProofStatus Status, string Rule, string? Detail = null,
    IReadOnlyList<string>? EffectIds = null)
{
    public static ProofVerdict NotYet(ProofFamily family, string rule) => new(family, ProofStatus.NotYet, rule);
    public static ProofVerdict Inconclusive(ProofFamily family, string rule, string? detail = null, IReadOnlyList<string>? effects = null)
        => new(family, ProofStatus.Inconclusive, rule, detail, effects);
    public static ProofVerdict Proved(ProofFamily family, string rule, IReadOnlyList<string>? effects = null, string? detail = null)
        => new(family, ProofStatus.Proved, rule, detail, effects);
    public static ProofVerdict Refuted(ProofFamily family, string rule, string? detail = null, IReadOnlyList<string>? effects = null)
        => new(family, ProofStatus.Refuted, rule, detail, effects);
}

public enum BindingMethod { ExactLabel, JevChoice }

/// <summary>How strongly a binding identifies its target. Weak bindings never prove anything.</summary>
public enum BindingStrength { Weak, Moderate, Strong }

/// <summary>
/// The control the step's descriptor was bound to, decided from the observation at
/// <see cref="ObservationRevision"/> before (or independent of) the action that used it.
/// </summary>
/// <param name="LabelTerms">Words of the bound control's own name that no other offered control shares. A control
/// whose landing page never echoes its own distinctive words is not established as the described target.</param>
public sealed record TargetBinding(
    string ElementRef, long ObservationRevision, BindingMethod Method, int CandidateCount,
    double? P = null, double? Margin = null, string? Label = null,
    IReadOnlyList<(string Ref, double P)>? RunnersUp = null,
    IReadOnlyList<string>? LabelTerms = null);

/// <summary>Calibrated cut points. The defaults are conservative starting values; shadow data sets the shipped ones.</summary>
public sealed record ProofThresholds(
    double StrongP = .80, double StrongMargin = .40, double ModerateP = .60, double ModerateMargin = .25,
    bool AllowSameOriginLink = false)
{
    public BindingStrength Classify(TargetBinding binding)
    {
        if (binding.Method == BindingMethod.ExactLabel) return BindingStrength.Strong;
        if (binding.P is not { } p || binding.Margin is not { } margin) return BindingStrength.Weak;
        if (p >= StrongP && margin >= StrongMargin) return BindingStrength.Strong;
        if (p >= ModerateP && margin >= ModerateMargin) return BindingStrength.Moderate;
        return BindingStrength.Weak;
    }
}

/// <summary>Everything a proof rule may read; the evaluator is a pure function of it.</summary>
public sealed record ProofInput(
    OutcomeStep Step,
    IReadOnlyList<Effect> Effects,
    InteractionAction? LastAction,
    TargetBinding? TargetBinding,
    InteractionObservation Observation);

public interface IProofEvaluator
{
    ProofVerdict Evaluate(ProofInput input);
}

/// <summary>One evaluation: after how many actions, the binding it read, and the verdict.</summary>
public sealed record ProofRecord(int Actions, ProofVerdict Verdict, TargetBinding? TargetBinding);
