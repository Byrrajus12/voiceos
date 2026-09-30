namespace VoiceOS.Core.Interaction;

/// <summary>
/// What one semantic step is for. A step states a desired operation/outcome, never a selector:
/// "Reach GitHub", "Search for X", "Locate Tauri in the results", "Open it".
/// </summary>
public enum PlanStepKind
{
    /// <summary>Get to the named site/page.</summary>
    Reach,
    /// <summary>Enter a query and apply it.</summary>
    Search,
    /// <summary>Make a described target visible/available in the current content.</summary>
    Locate,
    /// <summary>Activate a described target and follow it.</summary>
    Open,
    /// <summary>Any other bounded in-page operation (scroll, click a control, go back, media...).</summary>
    Act
}

/// <param name="Description">The operation in plain words; also what the decision model is asked to accomplish.</param>
/// <param name="Query">Grounded search text (Search only).</param>
/// <param name="Target">The described thing to find/open (Locate/Open, or Reach's site).</param>
/// <param name="Progress">Short present-tense status shown to the user while the step runs.</param>
public sealed record PlanStep(PlanStepKind Kind, string Description, string? Query = null,
    string? Target = null, string? Progress = null);

/// <summary>
/// The compiled form of one user request. <see cref="OriginalGoal"/> stays attached for the whole run;
/// the steps keep every meaningful intermediate intent (A → B → C is never collapsed to C).
/// </summary>
public sealed record InteractionPlan(string OriginalGoal, string FinalGoal, IReadOnlyList<PlanStep> Steps,
    int CurrentStepIndex = 0)
{
    public const int MaxSteps = 6;

    public PlanStep Current => Steps[CurrentStepIndex];
    public bool IsLastStep => CurrentStepIndex >= Steps.Count - 1;
    public IReadOnlyList<PlanStep> Completed => Steps.Take(CurrentStepIndex).ToArray();
    public IReadOnlyList<PlanStep> Remaining => Steps.Skip(CurrentStepIndex + 1).ToArray();

    public InteractionPlan Advance() => this with { CurrentStepIndex = Math.Min(CurrentStepIndex + 1, Steps.Count - 1) };
    public InteractionPlan WithCurrent(PlanStep step)
        => this with { Steps = Steps.Select((s, i) => i == CurrentStepIndex ? step : s).ToArray() };

    /// <summary>One lightweight step carrying the whole request; used when compiling would add nothing.</summary>
    public static InteractionPlan SingleStep(string utterance, PlanStepKind kind = PlanStepKind.Act, string? progress = null)
        => new(utterance, utterance, [new(kind, utterance, Target: kind == PlanStepKind.Open ? utterance : null, Progress: progress)]);
}
