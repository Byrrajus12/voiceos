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
    /// <summary>One history traversal; Target is "back" or "forward".</summary>
    History,
    /// <summary>Any other bounded in-page operation (scroll, click a control, go back, media...).</summary>
    Act
}

/// <summary>Which search surface a Search step is meant for. Unspecified means the broad, service-wide search.</summary>
public enum SearchScopeIntent { Unspecified, Global, CurrentResource, InPage, Collection }

/// <param name="Description">The operation in plain words; also what the decision model is asked to accomplish.</param>
/// <param name="Query">Grounded search text (Search only).</param>
/// <param name="Target">The described thing to find/open (Locate/Open, or Reach's site).</param>
/// <param name="Progress">Short present-tense status shown to the user while the step runs.</param>
/// <param name="ScopeIntent">Search only: the search surface the user meant.</param>
/// <param name="Produces">Locate only: names a value this step must find and hand to later steps as <c>${name}</c>.</param>
/// <param name="Reveal">Locate only: the target is a section of the page to bring into view. It is complete when the
/// section is in view and never activates a control.</param>
public sealed record PlanStep(PlanStepKind Kind, string Description, string? Query = null,
    string? Target = null, string? Progress = null, SearchScopeIntent ScopeIntent = SearchScopeIntent.Unspecified,
    string? Produces = null, bool Reveal = false);

/// <summary>
/// The compiled form of one user request. <see cref="OriginalGoal"/> stays attached for the whole run;
/// the steps keep every meaningful intermediate intent (A → B → C is never collapsed to C).
/// </summary>
public sealed record InteractionPlan(string OriginalGoal, string FinalGoal, IReadOnlyList<PlanStep> Steps,
    int CurrentStepIndex = 0)
{
    public const int MaxSteps = 6;

    /// <summary>Values earlier steps of this execution produced, by name. Lives only for this run.</summary>
    public IReadOnlyDictionary<string, string> Outputs { get; init; } = new Dictionary<string, string>();

    private static readonly System.Text.RegularExpressions.Regex Reference =
        new(@"\$\{([a-z][a-z0-9_]{0,31})\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The output names a step's text refers to.</summary>
    public static IReadOnlyList<string> References(PlanStep step)
        => new[] { step.Description, step.Query, step.Target, step.Progress }
            .SelectMany(static t => t is null ? [] : Reference.Matches(t).Select(static m => m.Groups[1].Value)).Distinct().ToArray();

    /// <summary>True when a later step needs the value this step produces; only then is it worth resolving.</summary>
    public bool IsConsumed(string name) => Remaining.Any(s => References(s).Contains(name));

    public InteractionPlan WithOutput(string name, string value)
        => this with { Outputs = new Dictionary<string, string>(Outputs) { [name] = value } };

    /// <summary>The current step with every <c>${name}</c> replaced by its captured value; Missing names an unresolved reference.</summary>
    public (InteractionPlan Plan, string? Missing) ResolveCurrent()
    {
        var step = Current;
        string? missing = null;
        string? Fill(string? text) => text is null ? null : Reference.Replace(text, m =>
        {
            if (Outputs.TryGetValue(m.Groups[1].Value, out var value)) return value;
            missing ??= m.Groups[1].Value;
            return m.Value;
        });
        var resolved = step with { Description = Fill(step.Description)!, Query = Fill(step.Query),
            Target = Fill(step.Target), Progress = Fill(step.Progress) };
        return (missing is null && resolved != step ? WithCurrent(resolved) : this, missing);
    }

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
