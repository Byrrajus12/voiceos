using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// M6D: Numeric relative volume parameters.
/// "volume up 10%" → AdjustVolumeStep(Up, 10); clamping; unparameterized unchanged.
/// </summary>
public class NumericVolumeTests
{
    // ── VolumeService.ComputeRelativeWithAmount pure-math ─────────────────────

    [Theory]
    [InlineData(0.5f, VolumeDirection.Up, 10, 0.6f)]
    [InlineData(0.5f, VolumeDirection.Down, 20, 0.3f)]
    [InlineData(0.3f, VolumeDirection.Down, 50, 0.0f)]   // lower clamp
    [InlineData(0.8f, VolumeDirection.Up, 50, 1.0f)]     // upper clamp
    public void ComputeRelativeWithAmount_CorrectClamping(
        float current, VolumeDirection dir, int amount, float expected)
    {
        var result = VolumeService.ComputeRelativeWithAmount(current, dir, amount);
        Assert.Equal(expected, result, precision: 4);
    }

    [Fact]
    public void ComputeRelativeWithAmount_DownAt0_ClampsTo0()
        => Assert.Equal(0f, VolumeService.ComputeRelativeWithAmount(0f, VolumeDirection.Down, 20));

    [Fact]
    public void ComputeRelativeWithAmount_UpAt100_ClampsTo1()
        => Assert.Equal(1f, VolumeService.ComputeRelativeWithAmount(1f, VolumeDirection.Up, 20));

    // ── Existing unparameterized AdjustVolume unchanged ───────────────────────

    [Fact]
    public void ComputeRelative_NoAmount_UsesDefaultStep()
    {
        float up = VolumeService.ComputeRelative(0.5f, VolumeDirection.Up);
        Assert.True(up > 0.5f && up <= 1.0f);

        float down = VolumeService.ComputeRelative(0.5f, VolumeDirection.Down);
        Assert.True(down < 0.5f && down >= 0.0f);
    }

    // ── AdjustVolumeStep carries optional amount ──────────────────────────────

    [Fact]
    public void AdjustVolumeStep_DefaultAmount_IsNull()
    {
        var step = new AdjustVolumeStep("s1", VolumeDirection.Up);
        Assert.Null(step.Amount);
    }

    [Fact]
    public void AdjustVolumeStep_ExplicitAmount_Stored()
    {
        var step = new AdjustVolumeStep("s1", VolumeDirection.Up, 10);
        Assert.Equal(10, step.Amount);
    }

    // ── ProgramExecutor: parameterized AdjustVolume forwarded correctly ───────

    [Fact]
    public async Task ProgramExecutor_AdjustVolume_WithAmount_ForwardsAmountToService()
    {
        var vol = new TrackingVolumeService();
        var program = new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Up, 10)]);
        var result = await BuildExecutor(vol).ExecuteAsync(program, []);

        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Up, vol.LastDirection);
        Assert.Equal(10, vol.LastAmount);
    }

    [Fact]
    public async Task ProgramExecutor_AdjustVolume_NoAmount_PassesNullToService()
    {
        var vol = new TrackingVolumeService();
        var program = new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Down)]);
        var result = await BuildExecutor(vol).ExecuteAsync(program, []);

        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Down, vol.LastDirection);
        Assert.Null(vol.LastAmount);
    }

    // ── PlanExecutor: simple adapter path ────────────────────────────────────

    [Fact]
    public async Task PlanExecutor_AdjustVolume_WithAmount_ForwardsAmount()
    {
        var vol = new TrackingVolumeService();
        var executor = new PlanExecutor(
            new NoOpLauncher(), new NoOpWindowService(), new NoOpMediaService(), vol,
            NullLogger<PlanExecutor>.Instance);

        var plan = new VoicePlan(VoiceAction.AdjustVolume,
            VolumeAdjust: VolumeDirection.Up,
            VolumeAdjustAmount: 10);

        var result = await executor.ExecuteAsync(plan, []);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(VolumeDirection.Up, vol.LastDirection);
        Assert.Equal(10, vol.LastAmount);
    }

    [Fact]
    public async Task PlanExecutor_AdjustVolume_NoAmount_PassesNull()
    {
        var vol = new TrackingVolumeService();
        var executor = new PlanExecutor(
            new NoOpLauncher(), new NoOpWindowService(), new NoOpMediaService(), vol,
            NullLogger<PlanExecutor>.Instance);

        var plan = new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: VolumeDirection.Down);

        var result = await executor.ExecuteAsync(plan, []);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(VolumeDirection.Down, vol.LastDirection);
        Assert.Null(vol.LastAmount);
    }

    // ── Absolute SetVolume unchanged ──────────────────────────────────────────

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(50, 0.5f)]
    [InlineData(100, 1.0f)]
    [InlineData(-10, 0f)]
    [InlineData(110, 1.0f)]
    public void ComputeAbsolute_UnchangedByM6D(int percent, float expected)
        => Assert.Equal(expected, VolumeService.ComputeAbsolute(percent), precision: 4);

    // ── SemanticProgramPlanner: compound path uses transcript amount ──────────

    [Fact]
    public void Planner_AdjustVolume_WithAmountInTranscript_ProducesStepWithAmount()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Up");

        // Transcript contains "10" → VolumeExtractor extracts 10
        var state = MakeState("turn volume up 10 percent");
        var program = Planner.TryBuildProgram(answers, state, new Dictionary<string, string>());

        Assert.NotNull(program);
        var step = Assert.IsType<AdjustVolumeStep>(program!.Steps[0]);
        Assert.Equal(VolumeDirection.Up, step.Direction);
        Assert.Equal(10, step.Amount);
    }

    [Fact]
    public void Planner_AdjustVolume_NoAmountInTranscript_ProducesStepWithNullAmount()
    {
        var answers = new Dictionary<string, JevAnswer>();
        AddCommand(answers);
        answers["unit_count"] = Choice("1");
        AddUnit(answers, 1, "AdjustVolume", volumeDir: "Down");

        var state = MakeState("turn it down");
        var program = Planner.TryBuildProgram(answers, state, new Dictionary<string, string>());

        Assert.NotNull(program);
        var step = Assert.IsType<AdjustVolumeStep>(program!.Steps[0]);
        Assert.Equal(VolumeDirection.Down, step.Direction);
        Assert.Null(step.Amount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly SemanticProgramPlanner Planner = new(0.35, 0.40);

    private static DecisionState MakeState(string transcript) => new(
        transcript, "notepad",
        [],
        [],
        [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
        [SnapDirection.Left, SnapDirection.Right]);

    private static JevAnswer Choice(string value, double confidence = 0.9)
        => new("choice", value, new Dictionary<string, double> { [value] = confidence }, confidence);

    private static JevAnswer Noul(double prob)
        => new("noul", prob >= 0.5 ? "true" : "false",
               new Dictionary<string, double> { ["noul"] = prob }, prob);

    private static void AddCommand(Dictionary<string, JevAnswer> d)
        => d["is_command"] = Noul(0.9);

    private static void AddUnit(Dictionary<string, JevAnswer> d, int i, string action,
        string? volumeDir = null)
    {
        d[$"unit_{i}_action_kind"] = Choice(action);
        if (volumeDir != null) d[$"unit_{i}_volume_direction"] = Choice(volumeDir);
        if (i > 1) d[$"unit_{i}_prior_ref"] = Noul(0.0);
    }

    private static ProgramExecutor BuildExecutor(IVolumeService? volume = null)
        => new(
            new NoOpLauncher2(),
            new NoOpCatalog(),
            new NoOpWindowService2(),
            new NoOpMediaService2(),
            volume ?? new TrackingVolumeService(),
            new NoOpWindowMoveService(),
            new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

    private sealed class TrackingVolumeService : IVolumeService
    {
        public VolumeDirection? LastDirection { get; private set; }
        public int? LastAmount { get; private set; }

        public ExecutionResult SetVolume(int pct) => ExecutionResult.Ok("ok");
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null)
        {
            LastDirection = dir;
            LastAmount = amount;
            return ExecutionResult.Ok("ok");
        }
    }

    private sealed class NoOpLauncher : IAppLauncher
    {
        public ExecutionResult Launch(string? id) => ExecutionResult.Fail(ExecutionStatus.AppNotFound, "noop");
    }

    private sealed class NoOpWindowService : IWindowService
    {
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Snap(SnapDirection dir, string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpMediaService : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpLauncher2 : IWindowAwareLauncher
    {
        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget t, IReadOnlyList<WindowCandidate> snap, CancellationToken ct = default)
            => Task.FromResult(AppWindowResult.Fail(ExecutionStatus.AppNotFound, "noop"));
        public Task<AppWindowResult> LaunchNewAsync(AppTarget t, CancellationToken ct = default)
            => Task.FromResult(AppWindowResult.Fail(ExecutionStatus.AppNotFound, "noop"));
    }

    private sealed class NoOpCatalog : IAppCatalog
    {
        public IReadOnlyList<AppEntry> GetAll() => [];
        public AppEntry? FindById(string id) => null;
    }

    private sealed class NoOpWindowService2 : IWindowService
    {
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Snap(SnapDirection dir, string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpMediaService2 : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpWindowMoveService : IWindowMoveService
    {
        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo t, DisplayTopology topo)
            => ExecutionResult.Fail(ExecutionStatus.PlatformError, "noop");
    }

    private sealed class NoOpTopologyService : IDisplayTopologyService
    {
        public DisplayTopology CaptureTopology() => DisplayTopology.Empty;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topo) => null;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topo) => null;
    }
}
