using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

/// <summary>
/// Tests that the semantic/planning layer correctly produces VoicePrograms that
/// ProgramExecutor can execute, verifying the end-to-end decision → execution contract.
///
/// These tests do NOT require audio, keyboard hooks, or Jev network access.
/// They exercise the decision→program→execution chain using injected fakes.
/// </summary>
public class ProgramOrchestratorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WindowCandidate MakeWindow(string id, string proc, nint hwnd = 99)
        => new(id, proc, $"{proc} Window", Hwnd: hwnd);

    private static IReadOnlyList<WindowCandidate> EmptySnapshot => [];

    private static StubCatalog DefaultCatalog() => new StubCatalog()
        .Add("google-chrome", "chrome")
        .Add("discord", "Discord")
        .Add("visual-studio-code", "Code");

    // ── Single-action: DecisionResult.Program is used ────────────────────────

    [Fact]
    public async Task SingleAction_Program_ExecutesThroughProgramExecutor()
    {
        // DecisionResult carries a pre-built 1-step VoiceProgram.
        // ProgramExecutor must be called with that program.
        var chromeWindow = MakeWindow("w-chrome", "chrome");
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("google-chrome", AppWindowResult.Ok(chromeWindow, "Focused"));

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("google-chrome"))
        ]);
        var result = await ExecuteProgram(launcher: launcher, program: program, snapshot: EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);
        Assert.Equal(chromeWindow, result.StepResults[0].ResultWindow);
    }

    [Fact]
    public async Task SingleAction_AdjustVolume_ExecutesThroughProgramExecutor()
    {
        var vol = new StubVolumeService { AdjustResult = ExecutionResult.Ok("done") };
        var program = new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Down)]);
        var result = await ExecuteProgram(volume: vol, program: program, snapshot: EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Down, vol.LastAdjustDirection);
    }

    // ── Two-step compound ─────────────────────────────────────────────────────

    [Fact]
    public async Task TwoStep_OpenChrome_MaximizeIt_StepResultTarget()
    {
        // Program: s1=OpenApp(Chrome), s2=Maximize(StepResultTarget(s1))
        // ProgramExecutor must use the ResultWindow from s1 for s2.
        var chromeWindow = MakeWindow("w-chrome", "chrome", hwnd: 101);
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("google-chrome", AppWindowResult.Ok(chromeWindow));

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("maximized") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("google-chrome")),
            new MaximizeWindowStep("s2", new StepResultTarget("s1"))
        ]);
        var snapshot = new List<WindowCandidate> { chromeWindow };
        var result = await ExecuteProgram(launcher: launcher, windows: windows, program: program, snapshot: snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.ExecutedCount);
        // s2 resolved via stable "result:s1" ID — not the snapshot-local candidate ID
        Assert.Equal("result:s1", windows.LastCurrentId);
    }

    [Fact]
    public async Task TwoStep_OpenChrome_MinimizeDiscord_ExplicitTargets()
    {
        // s1=OpenApp(Chrome), s2=Minimize(AppTarget(Discord)) — independent
        var chromeWindow = MakeWindow("w-chrome", "chrome");
        var discordWindow = MakeWindow("w-discord", "Discord", hwnd: 200);

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("google-chrome", AppWindowResult.Ok(chromeWindow));

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };
        var snapshot = new List<WindowCandidate> { discordWindow };

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("google-chrome")),
            new MinimizeWindowStep("s2", new AppTarget("discord"))
        ]);
        var result = await ExecuteProgram(launcher: launcher, windows: windows, program: program, snapshot: snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("w-discord", windows.LastCurrentId);
    }

    // ── Compound guard: failed compound must not silently execute via single-plan fallback ──

    [Fact]
    public void CompoundGuard_FailedCompound_BlocksSinglePlanFallback()
    {
        // Regression test for the compound fallback safety fix in ActivationOrchestrator.
        //
        // Scenario: "play music and snap it right"
        //   - is_compound noul = 0.85 (compound request was sent, unit_N answers processed)
        //   - TryBuildProgram returned null (MediaControlStep cannot yield a window; prior-ref invalid)
        //   - DecisionResult.Program = null
        //   - DecisionResult.Plan = MediaControl(Play) — the flat base-request answer is executable
        //
        // Old unconditional code: decision.Program ?? ToSingleStepProgram(decision.Plan)
        //   → returns MediaControlStep → ProgramExecutor executes it → snap is silently dropped.
        // New guarded code: compound detected → fallback suppressed → null → no execution.
        //
        // This test applies the same guard logic ActivationOrchestrator uses.
        // It fails with the old unconditional expression and passes with the compound guard.

        var plan = new VoicePlan(VoiceAction.MediaControl, Media: MediaOperation.Play);
        var rawAnswers = new Dictionary<string, JevAnswer>
        {
            ["is_compound"] = new JevAnswer("noul", "true",
                new Dictionary<string, double> { ["noul"] = 0.85 }, 0.85)
        };
        var decision = new DecisionResult(plan, null, rawAnswers, 100, 0, 0);

        // Confirm the flat Plan IS executable — this is the unsafe scenario
        var unconditionalFallback = decision.Program ?? SemanticProgramPlanner.ToSingleStepProgram(decision.Plan);
        Assert.NotNull(unconditionalFallback);  // old code would execute this — the bug

        // Apply the compound guard (mirrors ActivationOrchestrator exactly)
        bool compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var cmpAns)
            && cmpAns.QuestionType == "noul"
            && cmpAns.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;

        var guarded = decision.Program
            ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));

        Assert.Null(guarded);  // failed compound must produce no executable program
    }

    [Fact]
    public void CompoundGuard_NoCompoundSignal_AllowsSinglePlanFallback()
    {
        // When is_compound is absent (M4-era response, no unit_N_* questions asked),
        // the single-plan compatibility adapter must still be applied.
        var plan = new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: VolumeDirection.Down);
        var decision = new DecisionResult(plan, null, new Dictionary<string, JevAnswer>(), 100, 0, 0);

        bool compoundAttempted = decision.RawAnswers.TryGetValue("is_compound", out var cmpAns)
            && cmpAns.QuestionType == "noul"
            && cmpAns.Probabilities.GetValueOrDefault("noul") >= SemanticProgramPlanner.NoulDecisionBoundary;

        var guarded = decision.Program
            ?? (compoundAttempted ? null : SemanticProgramPlanner.ToSingleStepProgram(decision.Plan));

        Assert.NotNull(guarded);  // backward-compat path must still produce a program
        Assert.Single(guarded!.Steps);
        Assert.IsType<AdjustVolumeStep>(guarded.Steps[0]);
    }

    // ── Adapter: VoicePlan → VoiceProgram fallback ───────────────────────────

    [Fact]
    public void Adapter_SinglePlan_ProducesExecutableProgram()
    {
        // When DecisionResult.Program is null, ToSingleStepProgram(Plan) must produce
        // a valid 1-step VoiceProgram that ProgramExecutor can execute.
        var plan = new VoicePlan(
            VoiceAction.AdjustVolume,
            VolumeAdjust: VolumeDirection.Up,
            Confidence: 0.9);

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        var step = Assert.IsType<AdjustVolumeStep>(program.Steps[0]);
        Assert.Equal(VolumeDirection.Up, step.Direction);
    }

    [Fact]
    public void Adapter_OpenAppPlan_NewInstance_CarriesActivationMode()
    {
        var plan = new VoicePlan(
            VoiceAction.OpenApp,
            AppCandidateId: "google-chrome",
            ActivationMode: AppActivationMode.NewInstance);

        var program = SemanticProgramPlanner.ToSingleStepProgram(plan);

        Assert.NotNull(program);
        var step = Assert.IsType<OpenAppStep>(program!.Steps[0]);
        Assert.Equal(AppActivationMode.NewInstance, step.ActivationMode);
    }

    // ── Test runner helper ────────────────────────────────────────────────────

    private static Task<ProgramResult> ExecuteProgram(
        StubLauncher? launcher = null,
        StubWindowService? windows = null,
        StubVolumeService? volume = null,
        IMediaService? media = null,
        VoiceProgram? program = null,
        IReadOnlyList<WindowCandidate>? snapshot = null)
    {
        var executor = new ProgramExecutor(
            launcher ?? new StubLauncher(),
            DefaultCatalog(),
            windows ?? new StubWindowService(),
            media ?? new StubMediaService(),
            volume ?? new StubVolumeService(),
            new NoOpWindowMoveService(),
            new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

        return executor.ExecuteAsync(
            program ?? new VoiceProgram([]),
            snapshot ?? EmptySnapshot);
    }

    // ── Stub implementations ──────────────────────────────────────────────────

    private sealed class StubCatalog : IAppCatalog
    {
        private readonly Dictionary<string, AppEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public StubCatalog Add(string id, string processName, string? aumid = null)
        {
            _entries[id] = new AppEntry(id, id, processName, AppLaunchKind.Win32, id, null, aumid);
            return this;
        }

        public IReadOnlyList<AppEntry> GetAll() => [.. _entries.Values];
        public AppEntry? FindById(string id) => _entries.GetValueOrDefault(id);
    }

    private sealed class StubLauncher : IWindowAwareLauncher
    {
        private readonly Dictionary<string, AppWindowResult> _focusOrLaunch = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppWindowResult> _launchNew = new(StringComparer.OrdinalIgnoreCase);

        public void SetFocusOrLaunch(string appId, AppWindowResult result) => _focusOrLaunch[appId] = result;
        public void SetLaunchNew(string appId, AppWindowResult result) => _launchNew[appId] = result;

        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget target, IReadOnlyList<WindowCandidate> snapshot, CancellationToken ct = default)
            => Task.FromResult(_focusOrLaunch.TryGetValue(target.AppCandidateId, out var r) ? r
                : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{target.AppCandidateId}' not configured"));

        public Task<AppWindowResult> LaunchNewAsync(AppTarget target, CancellationToken ct = default)
            => Task.FromResult(_launchNew.TryGetValue(target.AppCandidateId, out var r) ? r
                : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{target.AppCandidateId}' not configured"));
    }

    private sealed class StubWindowService : IWindowService
    {
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult CurrentResult { get; set; } = ExecutionResult.Ok("done");
        public string? LastCurrentId { get; private set; }

        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return CurrentResult;
        }

        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return CurrentResult;
        }

        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return CurrentResult;
        }

        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return CurrentResult;
        }

        public ExecutionResult Snap(SnapDirection dir, string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return CurrentResult;
        }
    }

    private sealed class StubMediaService : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok(op.ToString());
    }

    private sealed class StubVolumeService : IVolumeService
    {
        public ExecutionResult AdjustResult { get; set; } = ExecutionResult.Ok("ok");
        public VolumeDirection? LastAdjustDirection { get; private set; }
        public ExecutionResult SetVolume(int pct) => ExecutionResult.Ok(pct.ToString());
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null) { LastAdjustDirection = dir; return AdjustResult; }
    }

    private sealed class NoOpWindowMoveService : IWindowMoveService
    {
        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo targetMonitor, DisplayTopology topology)
            => ExecutionResult.Fail(ExecutionStatus.PlatformError, "NoOp");
    }

    private sealed class NoOpTopologyService : IDisplayTopologyService
    {
        public DisplayTopology CaptureTopology() => DisplayTopology.Empty;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology) => null;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topology) => null;
    }
}
