using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Tests PlanExecutor routing and execution eligibility using stub service implementations.
/// </summary>
public class PlanExecutorTests
{
    private static IReadOnlyList<WindowCandidate> EmptySnapshot => [];

    private static IReadOnlyList<WindowCandidate> SnapshotWith(string id, string title, nint hwnd = 99, string proc = "proc")
        => [new WindowCandidate(id, proc, title, Hwnd: hwnd)];

    private static IReadOnlyList<WindowCandidate> SnapshotWithTwo(string proc)
        => [
            new WindowCandidate("w0", proc, $"{proc} - Window 1", Hwnd: 99),
            new WindowCandidate("w1", proc, $"{proc} - Window 2", Hwnd: 100),
        ];

    // ── None / Rejected / RequiresClarification must not execute ───────────────

    [Fact]
    public async Task None_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(new VoicePlan(VoiceAction.None), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    [Fact]
    public async Task Rejected_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.Rejected, RejectionReason: "low confidence"), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    [Fact]
    public async Task RequiresClarification_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, RequiresClarification: true), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    // ── OpenApp: NewInstance always launches ───────────────────────────────────

    [Fact]
    public async Task OpenApp_NewInstance_AlwaysLaunches_EvenWhenWindowExists()
    {
        var launcher = new StubLauncher();
        launcher.SetResult("chrome", ExecutionResult.Ok("Launched Chrome"));

        // Window exists for chrome, but NewInstance should bypass FocusOrLaunch
        var snapshot = SnapshotWith("w0", "Chrome", proc: "chrome");
        var result = await Build(launcher: launcher).ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp,
                AppCandidateId: "chrome",
                AppProcessName: "chrome",
                ActivationMode: AppActivationMode.NewInstance),
            snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("chrome", launcher.LastLaunchedId);
    }

    // ── OpenApp: FocusOrLaunch ─────────────────────────────────────────────────

    [Fact]
    public async Task OpenApp_FocusOrLaunch_NoMatchingWindow_Launches()
    {
        var launcher = new StubLauncher();
        launcher.SetResult("chrome", ExecutionResult.Ok("Launched Chrome"));

        var result = await Build(launcher: launcher).ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: "chrome", AppProcessName: "chrome"),
            EmptySnapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("chrome", launcher.LastLaunchedId);
    }

    [Fact]
    public async Task OpenApp_FocusOrLaunch_OneMatchingWindow_FocusesInsteadOfLaunching()
    {
        var windows = new StubWindowService();
        windows.FocusResult = ExecutionResult.Ok("Focused Chrome");

        var snapshot = SnapshotWith("w0", "Chrome", proc: "chrome");
        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: "chrome", AppProcessName: "chrome"),
            snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    [Fact]
    public async Task OpenApp_FocusOrLaunch_MultipleMatchingWindows_ReturnsWindowAmbiguous()
    {
        var snapshot = SnapshotWithTwo("chrome");
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: "chrome", AppProcessName: "chrome"),
            snapshot);

        Assert.Equal(ExecutionStatus.WindowAmbiguous, result.Status);
    }

    [Fact]
    public async Task OpenApp_FocusOrLaunch_NullProcessName_LaunchesDirectly()
    {
        // If AppProcessName is null (no process name in catalog), skip matching and launch.
        var launcher = new StubLauncher();
        launcher.SetResult("chrome", ExecutionResult.Ok("Launched Chrome"));

        var snapshot = SnapshotWith("w0", "Chrome", proc: "chrome");
        var result = await Build(launcher: launcher).ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: "chrome", AppProcessName: null),
            snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("chrome", launcher.LastLaunchedId);
    }

    [Fact]
    public async Task OpenApp_NullId_ReturnsAppNotFound()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.AppNotFound, result.Status);
    }

    [Fact]
    public async Task OpenApp_UnknownId_ReturnsAppNotFound()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.OpenApp, AppCandidateId: "unknown"), EmptySnapshot);
        Assert.Equal(ExecutionStatus.AppNotFound, result.Status);
    }

    // ── FocusWindow routing ────────────────────────────────────────────────────

    [Fact]
    public async Task FocusWindow_KnownId_RoutesToWindowService_ReturnsSuccess()
    {
        var windows = new StubWindowService();
        windows.FocusResult = ExecutionResult.Ok("Focused");

        var snapshot = SnapshotWith("w0", "Notepad");
        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.FocusWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    [Fact]
    public async Task FocusWindow_NullId_ReturnsWindowNotFound()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.FocusWindow, WindowCandidateId: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.WindowNotFound, result.Status);
    }

    [Fact]
    public async Task FocusWindow_IdNotInSnapshot_ReturnsWindowNotFound()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.FocusWindow, WindowCandidateId: "w99"),
            SnapshotWith("w0", "SomeWindow"));
        Assert.Equal(ExecutionStatus.WindowNotFound, result.Status);
    }

    [Fact]
    public async Task FocusWindow_ServiceReturnsWindowStale_ReturnsWindowStale()
    {
        var windows = new StubWindowService();
        windows.FocusResult = ExecutionResult.Fail(ExecutionStatus.WindowStale, "stale");

        var snapshot = SnapshotWith("w0", "Chrome");
        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.FocusWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.WindowStale, result.Status);
    }

    // ── Current-window actions: implicit (null) target uses foreground ──────────

    [Theory]
    [InlineData(VoiceAction.CloseCurrentWindow)]
    [InlineData(VoiceAction.MaximizeCurrentWindow)]
    [InlineData(VoiceAction.MinimizeCurrentWindow)]
    public async Task CurrentWindowAction_NullTarget_RoutesToWindowServiceWithNullId(VoiceAction action)
    {
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("done") };
        await Build(windows: windows).ExecuteAsync(new VoicePlan(action), EmptySnapshot);
        Assert.Null(windows.LastCurrentId); // null = foreground
    }

    // ── Named-target window actions ────────────────────────────────────────────

    [Fact]
    public async Task CloseCurrentWindow_WithNamedTarget_PassesCandidateId()
    {
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("closed") };
        var snapshot = SnapshotWith("w0", "Chrome");

        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.CloseCurrentWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    [Fact]
    public async Task MaximizeCurrentWindow_WithNamedTarget_PassesCandidateId()
    {
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("maximized") };
        var snapshot = SnapshotWith("w0", "VS Code");

        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.MaximizeCurrentWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    [Fact]
    public async Task MinimizeCurrentWindow_WithNamedTarget_PassesCandidateId()
    {
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };
        var snapshot = SnapshotWith("w0", "Discord");

        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.MinimizeCurrentWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    [Fact]
    public async Task CloseCurrentWindow_NamedTarget_StaleHwnd_ReturnsWindowStale_NoForegroundFallback()
    {
        // Stub returns WindowStale for a named target. Executor must NOT fall back to foreground.
        var windows = new StubWindowService
        {
            CurrentResult = ExecutionResult.Fail(ExecutionStatus.WindowStale, "stale")
        };
        var snapshot = SnapshotWith("w0", "Chrome");

        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.CloseCurrentWindow, WindowCandidateId: "w0"), snapshot);

        Assert.Equal(ExecutionStatus.WindowStale, result.Status);
    }

    // ── Snap routing ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SnapDirection.Left)]
    [InlineData(SnapDirection.Right)]
    public async Task SnapCurrentWindow_NullTarget_ReturnsSuccess(SnapDirection dir)
    {
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.SnapCurrentWindow, Snap: dir), EmptySnapshot);
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(dir, windows.LastSnapDirection);
        Assert.Null(windows.LastSnapWindowId); // null = foreground
    }

    [Fact]
    public async Task SnapCurrentWindow_WithNamedTarget_PassesCandidateId()
    {
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var snapshot = SnapshotWith("w0", "Chrome");

        var result = await Build(windows: windows).ExecuteAsync(
            new VoicePlan(VoiceAction.SnapCurrentWindow, Snap: SnapDirection.Left, WindowCandidateId: "w0"),
            snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastSnapWindowId);
        Assert.Equal(SnapDirection.Left, windows.LastSnapDirection);
    }

    [Fact]
    public async Task SnapCurrentWindow_MissingDirection_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.SnapCurrentWindow, Snap: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    // ── Media routing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MediaOperation.Play)]
    [InlineData(MediaOperation.Pause)]
    [InlineData(MediaOperation.Toggle)]
    [InlineData(MediaOperation.Next)]
    [InlineData(MediaOperation.Previous)]
    public async Task MediaControl_KnownOp_ReturnsSuccess(MediaOperation op)
    {
        var media = new StubMediaService { Result = ExecutionResult.Ok(op.ToString()) };
        var result = await Build(media: media).ExecuteAsync(
            new VoicePlan(VoiceAction.MediaControl, Media: op), EmptySnapshot);
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(op, media.LastOp);
    }

    [Fact]
    public async Task MediaControl_MissingOp_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.MediaControl, Media: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    // ── Volume routing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SetVolume_WithValue_ReturnsSuccess()
    {
        var volume = new StubVolumeService { SetResult = ExecutionResult.Ok("50%") };
        var result = await Build(volume: volume).ExecuteAsync(
            new VoicePlan(VoiceAction.SetVolume, VolumeValue: 50), EmptySnapshot);
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(50, volume.LastSetPercent);
    }

    [Fact]
    public async Task SetVolume_MissingValue_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.SetVolume, VolumeValue: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    [Theory]
    [InlineData(VolumeDirection.Up)]
    [InlineData(VolumeDirection.Down)]
    public async Task AdjustVolume_WithDirection_ReturnsSuccess(VolumeDirection dir)
    {
        var volume = new StubVolumeService { AdjustResult = ExecutionResult.Ok(dir.ToString()) };
        var result = await Build(volume: volume).ExecuteAsync(
            new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: dir), EmptySnapshot);
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal(dir, volume.LastAdjustDirection);
    }

    [Fact]
    public async Task AdjustVolume_MissingDirection_ReturnsNoAction()
    {
        var result = await Build().ExecuteAsync(
            new VoicePlan(VoiceAction.AdjustVolume, VolumeAdjust: null), EmptySnapshot);
        Assert.Equal(ExecutionStatus.NoAction, result.Status);
    }

    // ── Volume clamping (pure math) ────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(50, 0.5f)]
    [InlineData(100, 1.0f)]
    [InlineData(-10, 0f)]
    [InlineData(110, 1.0f)]
    public void VolumeService_ComputeAbsolute_ClampsCorrectly(int percent, float expected)
    {
        Assert.Equal(expected, VolumeService.ComputeAbsolute(percent), precision: 4);
    }

    [Fact]
    public void VolumeService_ComputeRelative_Up_IncreasesByStep()
    {
        float result = VolumeService.ComputeRelative(0.5f, VolumeDirection.Up);
        Assert.True(result > 0.5f);
        Assert.True(result <= 1.0f);
    }

    [Fact]
    public void VolumeService_ComputeRelative_Down_DecreasesByStep()
    {
        float result = VolumeService.ComputeRelative(0.5f, VolumeDirection.Down);
        Assert.True(result < 0.5f);
        Assert.True(result >= 0.0f);
    }

    [Fact]
    public void VolumeService_ComputeRelative_ClampsAtMaximum()
        => Assert.Equal(1.0f, VolumeService.ComputeRelative(0.99f, VolumeDirection.Up));

    [Fact]
    public void VolumeService_ComputeRelative_ClampsAtMinimum()
        => Assert.Equal(0.0f, VolumeService.ComputeRelative(0.01f, VolumeDirection.Down));

    // ── Execution result behavior ──────────────────────────────────────────────

    [Fact]
    public void ExecutionResult_Ok_HasSuccessStatus()
    {
        var r = ExecutionResult.Ok("done");
        Assert.Equal(ExecutionStatus.Success, r.Status);
        Assert.Equal("done", r.Detail);
    }

    [Fact]
    public void ExecutionResult_Noop_HasNoActionStatus()
    {
        var r = ExecutionResult.Noop("reason");
        Assert.Equal(ExecutionStatus.NoAction, r.Status);
    }

    [Fact]
    public void ExecutionResult_Fail_HasSpecifiedStatus()
    {
        var r = ExecutionResult.Fail(ExecutionStatus.WindowStale, "detail");
        Assert.Equal(ExecutionStatus.WindowStale, r.Status);
        Assert.Equal("detail", r.Detail);
    }

    // ── Builder / stubs ────────────────────────────────────────────────────────

    private static PlanExecutor Build(
        StubLauncher? launcher = null,
        StubWindowService? windows = null,
        StubMediaService? media = null,
        StubVolumeService? volume = null)
        => new(
            launcher ?? new StubLauncher(),
            windows ?? new StubWindowService(),
            media ?? new StubMediaService(),
            volume ?? new StubVolumeService(),
            NullLogger<PlanExecutor>.Instance);

    private sealed class StubLauncher : IAppLauncher
    {
        private readonly Dictionary<string, ExecutionResult> _results = new(StringComparer.OrdinalIgnoreCase);
        public string? LastLaunchedId { get; private set; }

        public void SetResult(string id, ExecutionResult result) => _results[id] = result;

        public ExecutionResult Launch(string? candidateId)
        {
            LastLaunchedId = candidateId;
            if (string.IsNullOrEmpty(candidateId))
                return ExecutionResult.Fail(ExecutionStatus.AppNotFound, "No ID");
            return _results.TryGetValue(candidateId, out var r) ? r
                : ExecutionResult.Fail(ExecutionStatus.AppNotFound, $"'{candidateId}' not found");
        }
    }

    private sealed class StubWindowService : IWindowService
    {
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult FocusResult { get; set; } = ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "not found");
        public ExecutionResult CurrentResult { get; set; } = ExecutionResult.Ok("done");
        public ExecutionResult SnapResult { get; set; } = ExecutionResult.Ok("snapped");

        public string? LastFocusedId { get; private set; }
        public string? LastCurrentId { get; private set; }
        public string? LastSnapWindowId { get; private set; }
        public SnapDirection? LastSnapDirection { get; private set; }

        public ExecutionResult Focus(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastFocusedId = windowCandidateId;
            if (string.IsNullOrEmpty(windowCandidateId))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No ID");
            if (!snapshot.Any(w => w.Id == windowCandidateId))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return FocusResult;
        }

        public ExecutionResult Close(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastCurrentId = windowCandidateId;
            return CurrentResult;
        }

        public ExecutionResult Maximize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastCurrentId = windowCandidateId;
            return CurrentResult;
        }

        public ExecutionResult Minimize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastCurrentId = windowCandidateId;
            return CurrentResult;
        }

        public ExecutionResult Snap(SnapDirection dir, string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastSnapDirection = dir;
            LastSnapWindowId = windowCandidateId;
            return SnapResult;
        }
    }

    private sealed class StubMediaService : IMediaService
    {
        public ExecutionResult Result { get; set; } = ExecutionResult.Ok("ok");
        public MediaOperation? LastOp { get; private set; }

        public ExecutionResult Send(MediaOperation op) { LastOp = op; return Result; }
    }

    private sealed class StubVolumeService : IVolumeService
    {
        public ExecutionResult SetResult { get; set; } = ExecutionResult.Ok("ok");
        public ExecutionResult AdjustResult { get; set; } = ExecutionResult.Ok("ok");
        public int? LastSetPercent { get; private set; }
        public VolumeDirection? LastAdjustDirection { get; private set; }
        public int? LastAdjustAmount { get; private set; }

        public ExecutionResult SetVolume(int percent) { LastSetPercent = percent; return SetResult; }
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null)
        {
            LastAdjustDirection = dir;
            LastAdjustAmount = amount;
            return AdjustResult;
        }
    }
}
