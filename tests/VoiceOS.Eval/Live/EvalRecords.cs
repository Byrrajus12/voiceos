using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;

namespace VoiceOS.Eval.Live;

// ── Observation DTOs (JSON-serializable, no live product dependency) ─────────────

public sealed record WindowRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>A top-level window. Rect is the visible frame (DWM extended frame bounds) in physical
/// pixels; MonitorIndex indexes <see cref="StateSnapshot.Monitors"/>. Enumerated is false for a raw
/// foreground window that normal window enumeration does not list.</summary>
public sealed record WindowInfo(string ProcessName, string Title, bool IsForeground, long Hwnd = 0,
    WindowRect? Rect = null, bool IsMinimized = false, bool IsMaximized = false, int? MonitorIndex = null,
    bool Enumerated = true);

public sealed record MonitorArea(int Index, bool IsPrimary, WindowRect WorkArea);

public sealed record TabInfo(int TabId, int WindowId, bool Active, string? Url, string? Title,
    BrowserTabProvenance Provenance, string? SessionId, long? LastUsedSequence)
{
    public string? Origin => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;
}

public sealed record StateSnapshot(DateTimeOffset CapturedAt, WindowInfo? Foreground,
    IReadOnlyList<WindowInfo> Windows, bool BrowserConnected, IReadOnlyList<TabInfo> Tabs,
    TabInfo? ActiveTab, int MonitorCount, IReadOnlyList<MonitorArea>? Monitors = null);

public sealed record RouteInfo(CommandRoute Route, double Confidence, RoutingReason Reason, string? Detail,
    MediaRequestKind MediaRequestKind, MediaOperation? MediaOperation, SemanticDestinationKind DestinationKind,
    string? DestinationName, string? ExplicitUrl, TabDisposition TabDisposition,
    SurfacePreference SurfacePreference, SemanticEndState EndState, GoalShape GoalShape,
    TaskRelation TaskRelation, ContextDependency ContextDependency, RequestedEntityKind RequestedEntity,
    string? NamedTabTarget);

public sealed record ScopeInfo(ExecutionScopeKind Kind, BrowserScopeKind? BrowserKind, int? TabId,
    string? Destination, string? NamedServiceHint, bool FocusOnly, string? Detail,
    string? NativeProcess, string? NativeAppId);

public sealed record StepResultInfo(string StepId, string Status, string? Message);

public sealed record BrowserExecutionInfo(string Completion, string? Detail, string? Url, string? Title,
    int Decisions, int Actions, int? TabId, int ChoiceCount);

public sealed record BrowserActivityInfo(string? Operation, string? TargetRole, string? TargetName,
    bool OpeningTab, string? Destination, string? Query);

public sealed record ExecutionInfo(string? DirectAction, string? DirectAppId, string? DirectWindowId,
    double? DirectConfidence, string? DirectTargetVerdict, IReadOnlyList<string> ProgramSteps,
    IReadOnlyList<StepResultInfo> StepResults, BrowserExecutionInfo? Browser,
    IReadOnlyList<BrowserActivityInfo> BrowserActivities,
    IReadOnlyList<AttemptedDirectStep>? AttemptedDirectSteps = null);

/// <summary>A direct step the executor actually attempted (its StepId has a result), with the typed
/// operands of media and volume steps.</summary>
public sealed record AttemptedDirectStep(string StepId, string Kind, MediaOperation? MediaOperation = null,
    int? VolumeValue = null, VolumeDirection? VolumeDirection = null, int? VolumeAmount = null);

public sealed record LatencyStageInfo(string Name, double StartMs, double ElapsedMs);

public sealed record LatencyInfo(double TotalPostSttMs, double? FirstActionMs,
    IReadOnlyList<LatencyStageInfo> Stages, IReadOnlyDictionary<string, double> StageTotals,
    IReadOnlyDictionary<string, int> StageCounts);

/// <summary>modelStages counts trace stages named route/direct_decision/normalization/browser_decision/
/// text_value/completion_confirmation. It is a lower bound of model calls: router and scope
/// sub-calls are not individually traced.</summary>
public sealed record CountInfo(int Actions, int BrowserDecisions, int DirectSteps, int TabsCreated,
    int ModelStages, int Clarifications);

public enum OutcomeClass { Complete, Clarify, Unsupported, Failed, Timeout, Unavailable }

public sealed record TurnRecord(
    int Index, string Transcript, string ActivationId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Lane, string ProductOutcome, OutcomeClass OutcomeClass, string? TerminalPhase, string? TerminalStatus,
    string? Failure, RouteInfo? InitialRoute, RouteInfo? Route, bool ReroutedFromDirect, ScopeInfo? Scope,
    ExecutionInfo Execution, LatencyInfo Latency, CountInfo Counts, StateSnapshot Initial, StateSnapshot Final,
    IReadOnlyList<CheckResult> Checks, Classification Classification)
{
    /// <summary>Failed advisory checks (efficiency budgets, outcome/scope preference) of a turn whose
    /// correctness passed; recorded separately so they never change the classification.</summary>
    public IReadOnlyList<string> EfficiencyMisses { get; init; } = [];
    public IReadOnlyList<JevDiagnostics.SummaryRecord> Heads { get; init; } = [];
}

public enum CheckCategory { Outcome, Route, Scope, SideEffect, Efficiency, EndState, Precondition }

public sealed record CheckResult(string Name, CheckCategory Category, bool Passed, string? Expected, string? Actual);

public enum Classification
{
    Pass, CorrectClarify, Partial, UnnecessaryClarify, MissedClarify, WrongRoute, WrongScope, WrongTarget,
    WrongAction, FalseSuccess, ExecutionFailure, Unsupported, Timeout, SetupFailure, EnvironmentMismatch,
    UnexpectedOutcome, HarnessError, Skipped, InfrastructureUnavailable
}

public sealed record ScenarioResult(string RunId, string ScenarioId, string Name, string Family,
    IReadOnlyList<string> Tags, int Attempt, DateTimeOffset StartedAt, double WallMs, Classification Classification,
    bool Success, IReadOnlyList<CheckResult> Preconditions, IReadOnlyList<StepOutcome> Setup,
    IReadOnlyList<StepOutcome> Cleanup, IReadOnlyList<TurnRecord> Turns, string? Note)
{
    public static readonly IReadOnlySet<Classification> SuccessSet = new HashSet<Classification>
        { Classification.Pass, Classification.CorrectClarify };
}

public sealed record StepOutcome(string Step, bool Ok, string? Detail);
