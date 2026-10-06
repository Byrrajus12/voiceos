using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;

namespace VoiceOS.Eval.Live;

/// <summary>Normalized, validated scenario model consumed by the selector, runner, and evaluator.</summary>
public enum PreconditionKind { CompanionConnected, AppInstalled, ForegroundProcess, WindowOpen, BrowserTabs, ActiveTabOrigin, MinMonitors }

public sealed record Precondition(PreconditionKind Kind, int WaitSeconds = 40, string? App = null,
    string? Process = null, int Min = 0, string? Contains = null, int Count = 0);

public enum SetupStepKind { StartProcess, FocusWindow, Wait, CloseNewTabs, CloseFixtureTab }

public sealed record SetupStep(SetupStepKind Kind, string? File = null, string? Args = null,
    string? Process = null, int Ms = 0);

public sealed record ScenarioSafety(bool Unattended = true, bool MutatesExternalState = false, string? Note = null);

public enum ExpectedOutcome { Complete, Clarify, Unsupported, Failed }

/// <summary>Which scope the run should have landed in, derived from ExecutionScopeDecision.</summary>
public enum ScopeExpectation { Direct, ActiveTab, ExistingNamedTab, RecentOwnedTaskTab, NewTaskTab, Native, TextTransform, Clarify }

public sealed record StringSetExpectation(IReadOnlyList<string>? Include = null, IReadOnlyList<string>? Exclude = null);

public sealed record NewTabsExpectation(int? Min = null, int? Max = null);

/// <summary>Mirrors AdjustVolumeStep: direction, and optionally the exact amount.</summary>
public sealed record AdjustVolumeExpectation(VolumeDirection Direction, int? Amount = null);

public sealed record FinalExpectation(string? ForegroundProcess = null, string? ForegroundTitleContains = null,
    string? ActiveTabOriginContains = null, string? ActiveTabUrlContains = null, string? ActiveTabTitleContains = null,
    IReadOnlyList<WindowExpectation>? Windows = null, string? LastActivatedTargetContains = null);

public enum SnapSide { Left, Right }

/// <summary>Observable end state of a top-level window. Select by process/title, or by the window
/// that was foreground when the turn began; then assert existence and/or geometry state.</summary>
public sealed record WindowExpectation(
    string? Process = null, string? TitleContains = null, bool InitialForeground = false,
    bool Exists = true, bool IsNew = false, bool? Minimized = null, bool? Maximized = null,
    SnapSide? Snapped = null, bool MonitorChanged = false)
{
    public string Describe() => string.Join(" ", new[]
    {
        InitialForeground ? "initial-foreground" : null, Process,
        TitleContains is null ? null : $"title~{TitleContains}",
        Exists ? null : "absent", IsNew ? "new" : null, Minimized is { } min ? $"minimized={min}" : null,
        Maximized is { } max ? $"maximized={max}" : null, Snapped is { } side ? $"snapped={side}" : null,
        MonitorChanged ? "monitor-changed" : null
    }.Where(static p => p is not null));
}

/// <summary>
/// Expected result of one turn. <c>Outcome</c> is the preferred outcome, whose positive checks
/// (route, scope, operations, end state) the expectation describes. <c>AcceptedOutcomes</c>, when
/// present, lists every outcome that is safe and correct for the turn; an accepted outcome other
/// than the preferred one passes on the guard checks alone and records an advisory preference miss.
/// <c>PreferredScope</c> records a correct but less-preferred scope as an advisory miss.
/// <c>EfficiencyAdvisory</c> records action/decision budget misses without failing correctness.
/// </summary>
public sealed record Expectation(
    ExpectedOutcome? Outcome = null,
    IReadOnlyList<CommandRoute>? Route = null,
    IReadOnlyList<ScopeExpectation>? Scope = null,
    StringSetExpectation? DirectSteps = null,
    StringSetExpectation? BrowserOperations = null,
    bool? NoMediaCommand = null,
    NewTabsExpectation? NewTabs = null,
    int? MaxActions = null,
    int? MaxBrowserDecisions = null,
    FinalExpectation? Final = null,
    MediaOperation? MediaOperation = null,
    int? SetVolume = null,
    AdjustVolumeExpectation? AdjustVolume = null,
    IReadOnlyList<ExpectedOutcome>? AcceptedOutcomes = null,
    IReadOnlyList<ScopeExpectation>? PreferredScope = null,
    bool EfficiencyAdvisory = false)
{
    /// <summary>The preferred outcome: explicit <c>Outcome</c>, else the first accepted outcome, else Complete.</summary>
    public ExpectedOutcome PrimaryOutcome => Outcome ?? AcceptedOutcomes?.FirstOrDefault() ?? ExpectedOutcome.Complete;

    public IReadOnlyList<ExpectedOutcome> AllAcceptedOutcomes => AcceptedOutcomes is { Count: > 0 } accepted
        ? accepted.Prepend(PrimaryOutcome).Distinct().ToArray() : [PrimaryOutcome];
}

/// <summary>Before: setup-style steps run between turns (e.g. focusing a window, closing tabs the scenario opened).</summary>
/// <param name="Choose">Instead of speaking a command, answers the pending choice with the option whose words contain this text.</param>
public sealed record Turn(string Transcript, Expectation? Expect = null, int SettleMs = 800,
    IReadOnlyList<SetupStep>? Before = null, string? Choose = null);

public sealed record Scenario(
    string Id, string Name, string Family, IReadOnlyList<string> Tags, ScenarioSafety Safety,
    IReadOnlyList<Precondition> Preconditions, IReadOnlyList<SetupStep> Setup, IReadOnlyList<SetupStep> Cleanup,
    IReadOnlyList<Turn> Turns, int TimeoutSeconds = 60, int SettleMs = 800);

public sealed class ScenarioLoadException(string message) : Exception(message);
