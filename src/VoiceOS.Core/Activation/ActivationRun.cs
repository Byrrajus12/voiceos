using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Core.Activation;

/// <summary>Where the recognized transcript of an activation came from.</summary>
public enum ActivationSource { Voice, InjectedTranscript }

/// <summary>
/// Passive structured record of one activation, filled in as the production pipeline runs.
/// Control flow never reads it; it exists so callers (e.g. the live eval harness) can observe
/// the same route, scope, execution, and timing facts that are otherwise only logged.
/// </summary>
public sealed class ActivationRun
{
    private readonly object _lock = new();
    private readonly List<ApplicationInteractionSnapshot> _snapshots = [];
    private readonly List<BrowserActivity> _browserActivities = [];

    public ActivationRun(string activationId, InteractionKind kind, ActivationSource source)
    {
        ActivationId = activationId;
        Kind = kind;
        Source = source;
        Lane = kind == InteractionKind.Dictation ? "Dictation" : "Unrouted";
    }

    public string ActivationId { get; }
    public InteractionKind Kind { get; }
    public ActivationSource Source { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; internal set; }
    public DateTimeOffset? PostSttStart { get; internal set; }
    public string? Transcript { get; internal set; }

    /// <summary>Same values the "Activation final" log line reports.</summary>
    public string Lane { get; internal set; }
    public string Outcome { get; internal set; } = "Failed";
    public int ActionCount { get; internal set; }

    public CommandRouteDecision? InitialRoute { get; internal set; }
    public IReadOnlyList<JevDiagnostics.SummaryRecord> RouteHeads => Trace is { Heads.Count: > 0 } trace ? trace.Heads :
        (InitialRoute?.RawAnswers ?? new Dictionary<string, JevAnswer>())
            .Select(x => JevDiagnostics.Record("route", x.Key, x.Value))
            .Concat(((SpeculativeDecision ?? Decision)?.RawAnswers ?? new Dictionary<string, JevAnswer>())
                .Select(x => JevDiagnostics.Record("direct", x.Key, x.Value))).ToArray();
    /// <summary>The route after any direct-to-browser reroute.</summary>
    public CommandRouteDecision? Route { get; internal set; }
    public bool ReroutedFromDirect { get; internal set; }
    public DirectTargetVerdict? DirectTargetVerdict { get; internal set; }
    public ExecutionContextSnapshot? ExecutionContext { get; internal set; }
    public ExecutionScopeDecision? Scope { get; internal set; }
    public BrowserInteractionOutcome? BrowserOutcome { get; internal set; }
    public DecisionResult? Decision { get; internal set; }
    public DecisionResult? SpeculativeDecision { get; internal set; }
    public Task<DecisionResult>? SpeculativeTask { get; internal set; }
    public bool SpeculativeDirectUsed { get; internal set; }
    public double? SpeculativeDirectMs { get; internal set; }
    public FrontDoorVerdict? FrontDoor { get; internal set; }
    public List<FrontDoorEvaluation> FrontDoorEvaluations { get; } = [];
    public double? CompletedPostSttMs { get; internal set; }
    public VoiceProgram? Program { get; internal set; }
    public ProgramResult? ProgramResult { get; internal set; }
    public DateTimeOffset? JevStart { get; internal set; }
    public DateTimeOffset? JevEnd { get; internal set; }
    public LatencyTrace? Trace { get; internal set; }
    /// <summary>An exception that escaped the pipeline (logged and surfaced as an error by the product).</summary>
    public Exception? Failure { get; internal set; }

    public IReadOnlyList<ApplicationInteractionSnapshot> Snapshots
    {
        get { lock (_lock) return _snapshots.ToArray(); }
    }

    public IReadOnlyList<BrowserActivity> BrowserActivities
    {
        get { lock (_lock) return _browserActivities.ToArray(); }
    }

    /// <summary>The last user-visible terminal state the product published for this activation.</summary>
    public ApplicationInteractionSnapshot? TerminalSnapshot
    {
        get
        {
            lock (_lock)
                return _snapshots.LastOrDefault(static s => s.Phase is ApplicationInteractionPhase.Succeeded
                    or ApplicationInteractionPhase.Failed or ApplicationInteractionPhase.NeedsChoice
                    or ApplicationInteractionPhase.Unavailable);
        }
    }

    internal void AddSnapshot(ApplicationInteractionSnapshot snapshot)
    {
        lock (_lock) _snapshots.Add(snapshot);
    }

    internal void AddBrowserActivity(BrowserActivity activity)
    {
        lock (_lock) _browserActivities.Add(activity);
    }
}

public sealed record FrontDoorEvaluation(string Stage, FrontDoorVerdictKind Kind, IReadOnlyList<string> Reasons,
    string? DirectAction, bool DistributionPresent, double? DirectP, double? BrowserP, double? TextP);
