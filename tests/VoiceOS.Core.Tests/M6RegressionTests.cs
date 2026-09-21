using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests;

/// <summary>
/// M6E regression suite: compact deterministic coverage across representative M6 scenarios.
/// Uses typed VoiceProgram inputs directly — no HTTP, no audio, no real Win32 calls.
/// Covers: open/focus, close/minimize/maximize, snap, move monitor, media, set volume,
/// relative volume, compounds, same-app ambiguity, and failure distinctions.
/// </summary>
public class M6RegressionTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static AppTarget Chrome => new("chrome");
    private static AppTarget Discord => new("discord");
    private static AppTarget VsCode => new("visual-studio-code");

    private static WindowCandidate Window(string id, string proc, nint hwnd = 99)
        => new(id, proc, $"{proc} - Window", Hwnd: hwnd);

    private static readonly IReadOnlyList<WindowCandidate> Empty = [];

    // Two-monitor topology: internal left + external right (primary)
    private static readonly MonitorInfo InternalMonitor = new(
        HMonitor: 1001,
        Bounds: new MonitorBounds(-2560, 0, 0, 1440),
        WorkArea: new MonitorBounds(-2560, 0, 0, 1440),
        IsPrimary: false,
        GdiDeviceName: "\\\\.\\DISPLAY1",
        DevicePath: null,
        FriendlyName: "Built-in Display",
        ConnectionKind: DisplayConnectionKind.Internal);

    private static readonly MonitorInfo ExternalMonitor = new(
        HMonitor: 1002,
        Bounds: new MonitorBounds(0, 0, 2560, 1440),
        WorkArea: new MonitorBounds(0, 0, 2560, 1440),
        IsPrimary: true,
        GdiDeviceName: "\\\\.\\DISPLAY2",
        DevicePath: null,
        FriendlyName: null,
        ConnectionKind: DisplayConnectionKind.External);

    private static DisplayTopology TwoMonitorTopology => new([InternalMonitor, ExternalMonitor]);

    // ── App open / focus ──────────────────────────────────────────────────────

    [Fact]
    public async Task R_OpenApp_NoWindow_Launches()
    {
        var launchedWindow = Window("w-chrome", "chrome");
        var launcher = new StubLauncher().SetFocus("chrome", AppWindowResult.Ok(launchedWindow));
        var result = await Build(launcher).ExecuteAsync(
            new VoiceProgram([new OpenAppStep("s1", Chrome)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(launchedWindow, result.StepResults[0].ResultWindow);
    }

    [Fact]
    public async Task R_OpenApp_NewInstance_Launches()
    {
        var newWindow = Window("w-chrome2", "chrome", hwnd: 200);
        var launcher = new StubLauncher().SetLaunchNew("chrome", AppWindowResult.Ok(newWindow));
        var result = await Build(launcher).ExecuteAsync(
            new VoiceProgram([new OpenAppStep("s1", Chrome, AppActivationMode.NewInstance)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(newWindow, result.StepResults[0].ResultWindow);
    }

    // ── Close / minimize / maximize ───────────────────────────────────────────

    [Fact]
    public async Task R_CloseCurrentWindow_CurrentTarget_UsesNull()
    {
        var windows = new TrackingWindowService { CurrentResult = ExecutionResult.Ok("closed") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new CloseWindowStep("s1", new CurrentWindowTarget())]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Null(windows.LastCurrentId);
    }

    [Fact]
    public async Task R_MinimizeApp_SingleWindow_Resolves()
    {
        var snapshot = new List<WindowCandidate> { Window("w0", "discord", hwnd: 50) };
        var windows = new TrackingWindowService { CurrentResult = ExecutionResult.Ok("minimized") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new MinimizeWindowStep("s1", Discord)]), snapshot);
        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    [Fact]
    public async Task R_MaximizeApp_SingleWindow_Resolves()
    {
        var snapshot = new List<WindowCandidate> { Window("w0", "Code", hwnd: 60) };
        var windows = new TrackingWindowService { CurrentResult = ExecutionResult.Ok("maximized") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new MaximizeWindowStep("s1", VsCode)]), snapshot);
        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    // ── Snap ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R_SnapCurrentWindow_Left_CurrentTarget()
    {
        var windows = new TrackingWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new SnapWindowStep("s1", new CurrentWindowTarget(), SnapDirection.Left)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(SnapDirection.Left, windows.LastSnapDir);
        Assert.Null(windows.LastCurrentId);
    }

    [Fact]
    public async Task R_SnapApp_SingleWindow_Resolves()
    {
        var snapshot = new List<WindowCandidate> { Window("w0", "chrome", hwnd: 10) };
        var windows = new TrackingWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new SnapWindowStep("s1", Chrome, SnapDirection.Right)]), snapshot);
        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastSnapWindowId);
        Assert.Equal(SnapDirection.Right, windows.LastSnapDir);
    }

    // ── Move monitor ──────────────────────────────────────────────────────────

    [Fact]
    public async Task R_MoveWindow_ToExternalMonitor_CurrentTarget()
    {
        var mover = new TrackingMoveService();
        var topo = new TrackingTopologyService(InternalMonitor, TwoMonitorTopology);

        var result = await Build(windowMover: mover, topoService: topo).ExecuteAsync(
            new VoiceProgram([new MoveWindowStep("s1", new CurrentWindowTarget(), new ExternalMonitor())]),
            Empty, TwoMonitorTopology);

        Assert.True(result.AllSucceeded);
        Assert.Equal(ExternalMonitor.HMonitor, mover.LastTargetHMonitor);
    }

    [Fact]
    public void R_MoveWindow_UnsupportedTarget_ParseReturnsNull()
    {
        // UnsupportedExplicitTarget never reaches execution — ParseMonitorTarget returns null
        // and the plan/program omits the step. Ordinal monitor refs ("monitor 2", "second monitor")
        // must never be routed to execution.
        var target = SemanticProgramPlanner.ParseMonitorTargetPublic("UnsupportedExplicitTarget");
        Assert.Null(target);
    }

    // ── Media ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MediaOperation.Play)]
    [InlineData(MediaOperation.Pause)]
    [InlineData(MediaOperation.Next)]
    [InlineData(MediaOperation.Previous)]
    public async Task R_MediaControl_AllOps_Succeed(MediaOperation op)
    {
        var media = new TrackingMediaService();
        var result = await Build(media: media).ExecuteAsync(
            new VoiceProgram([new MediaControlStep("s1", op)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(op, media.LastOp);
    }

    // ── Set volume ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task R_SetVolume_AbsoluteValues_Succeed(int pct)
    {
        var vol = new TrackingVolumeService();
        var result = await Build(volume: vol).ExecuteAsync(
            new VoiceProgram([new SetVolumeStep("s1", pct)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(pct, vol.LastSetPct);
    }

    // ── Relative volume ───────────────────────────────────────────────────────

    [Fact]
    public async Task R_AdjustVolume_Up10_ForwardsAmount()
    {
        var vol = new TrackingVolumeService();
        var result = await Build(volume: vol).ExecuteAsync(
            new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Up, 10)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Up, vol.LastDir);
        Assert.Equal(10, vol.LastAmount);
    }

    [Fact]
    public async Task R_AdjustVolume_Down20_ForwardsAmount()
    {
        var vol = new TrackingVolumeService();
        var result = await Build(volume: vol).ExecuteAsync(
            new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Down, 20)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Down, vol.LastDir);
        Assert.Equal(20, vol.LastAmount);
    }

    [Fact]
    public async Task R_AdjustVolume_NoAmount_PassesNull()
    {
        var vol = new TrackingVolumeService();
        var result = await Build(volume: vol).ExecuteAsync(
            new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Up)]), Empty);
        Assert.True(result.AllSucceeded);
        Assert.Null(vol.LastAmount);
    }

    [Fact]
    public void R_RelativeVolume_LowerClamp_At0()
        => Assert.Equal(0f, VolumeService.ComputeRelativeWithAmount(0.1f, VolumeDirection.Down, 50));

    [Fact]
    public void R_RelativeVolume_UpperClamp_At100()
        => Assert.Equal(1f, VolumeService.ComputeRelativeWithAmount(0.9f, VolumeDirection.Up, 50));

    // ── Compound programs ─────────────────────────────────────────────────────

    [Fact]
    public async Task R_Compound_OpenChrome_TurnVolumeDown_TwoStepsSucceed()
    {
        var chromeWindow = Window("w-chrome", "chrome");
        var launcher = new StubLauncher().SetFocus("chrome", AppWindowResult.Ok(chromeWindow));
        var vol = new TrackingVolumeService();

        var result = await Build(launcher, volume: vol).ExecuteAsync(
            new VoiceProgram([
                new OpenAppStep("s1", Chrome),
                new AdjustVolumeStep("s2", VolumeDirection.Down, 10)
            ]), Empty);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.ExecutedCount);
        Assert.Equal(VolumeDirection.Down, vol.LastDir);
        Assert.Equal(10, vol.LastAmount);
    }

    [Fact]
    public async Task R_Compound_OpenChrome_SnapItRight_StepResultTarget()
    {
        var chromeWindow = Window("w-chrome", "chrome", hwnd: 77);
        var launcher = new StubLauncher().SetFocus("chrome", AppWindowResult.Ok(chromeWindow));
        var windows = new TrackingWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var result = await Build(launcher, windows: windows).ExecuteAsync(
            new VoiceProgram([
                new OpenAppStep("s1", Chrome),
                new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
            ]), Empty);

        Assert.True(result.AllSucceeded);
        Assert.Equal("result:s1", windows.LastSnapWindowId);
        Assert.Equal(SnapDirection.Right, windows.LastSnapDir);
    }

    // ── Same-app ambiguity ────────────────────────────────────────────────────

    [Fact]
    public async Task R_SameApp_TwoChromeWindows_IsAmbiguous()
    {
        var snapshot = new List<WindowCandidate>
        {
            Window("w0", "chrome", hwnd: 10),
            Window("w1", "chrome", hwnd: 20)
        };
        var windows = new TrackingWindowService { CurrentResult = ExecutionResult.Ok("closed") };

        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new CloseWindowStep("s1", Chrome)]), snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
    }

    [Fact]
    public async Task R_SameApp_OneChromeWindow_Resolves()
    {
        var snapshot = new List<WindowCandidate> { Window("w0", "chrome", hwnd: 10) };
        var windows = new TrackingWindowService { CurrentResult = ExecutionResult.Ok("closed") };

        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new CloseWindowStep("s1", Chrome)]), snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    // ── Failure distinctions (M6E groundwork) ─────────────────────────────────

    [Fact]
    public async Task R_Failure_StaleWindow_SurfacesAsExecutionFailed()
    {
        var snapshot = new List<WindowCandidate> { Window("w0", "discord", hwnd: 50) };
        var windows = new TrackingWindowService
        {
            CurrentResult = ExecutionResult.Fail(ExecutionStatus.WindowStale, "stale HWND")
        };

        var result = await Build(windows: windows).ExecuteAsync(
            new VoiceProgram([new MinimizeWindowStep("s1", Discord)]), snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
        Assert.Contains("stale", result.StepResults[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R_Failure_AppNotFound_IsTypedException()
    {
        var result = await Build().ExecuteAsync(
            new VoiceProgram([new MinimizeWindowStep("s1", new AppTarget("unknown-app"))]),
            Empty);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
    }

    [Fact]
    public async Task R_Failure_FirstStep_SecondStepNeverRuns()
    {
        var vol = new TrackingVolumeService();
        var result = await Build(volume: vol).ExecuteAsync(
            new VoiceProgram([
                new MinimizeWindowStep("s1", new AppTarget("unknown-app")),
                new AdjustVolumeStep("s2", VolumeDirection.Down, 10)
            ]), Empty);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);
        Assert.Null(vol.LastDir);  // s2 never ran
    }

    // ── Builder / stubs ───────────────────────────────────────────────────────

    private static ProgramExecutor Build(
        StubLauncher? launcher = null,
        TrackingWindowService? windows = null,
        TrackingMediaService? media = null,
        TrackingVolumeService? volume = null,
        IWindowMoveService? windowMover = null,
        IDisplayTopologyService? topoService = null)
        => new(
            launcher ?? new StubLauncher(),
            DefaultCatalog(),
            windows ?? new TrackingWindowService(),
            media ?? new TrackingMediaService(),
            volume ?? new TrackingVolumeService(),
            windowMover ?? new NoOpMoveService(),
            topoService ?? new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

    private static IAppCatalog DefaultCatalog() => new StubCatalog()
        .Add("chrome", "chrome")
        .Add("discord", "Discord")
        .Add("visual-studio-code", "Code");

    private sealed class StubCatalog : IAppCatalog
    {
        private readonly Dictionary<string, AppEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public StubCatalog Add(string id, string proc)
        {
            _entries[id] = new AppEntry(id, id, proc, AppLaunchKind.Win32, id, null, null);
            return this;
        }

        public IReadOnlyList<AppEntry> GetAll() => [.. _entries.Values];
        public AppEntry? FindById(string id) => _entries.GetValueOrDefault(id);
    }

    private sealed class StubLauncher : IWindowAwareLauncher
    {
        private readonly Dictionary<string, AppWindowResult> _focus = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AppWindowResult> _new = new(StringComparer.OrdinalIgnoreCase);

        public StubLauncher SetFocus(string id, AppWindowResult r) { _focus[id] = r; return this; }
        public StubLauncher SetLaunchNew(string id, AppWindowResult r) { _new[id] = r; return this; }

        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget t, IReadOnlyList<WindowCandidate> snap, CancellationToken ct = default)
            => Task.FromResult(_focus.TryGetValue(t.AppCandidateId, out var r) ? r
                : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{t.AppCandidateId}' not configured"));

        public Task<AppWindowResult> LaunchNewAsync(AppTarget t, CancellationToken ct = default)
            => Task.FromResult(_new.TryGetValue(t.AppCandidateId, out var r) ? r
                : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{t.AppCandidateId}' not configured"));
    }

    private sealed class TrackingWindowService : IWindowService
    {
        public ExecutionResult FocusResult { get; set; } = ExecutionResult.Ok("focused");
        public ExecutionResult CurrentResult { get; set; } = ExecutionResult.Ok("done");
        public ExecutionResult SnapResult { get; set; } = ExecutionResult.Ok("snapped");
        public nint GetForegroundWindowHwnd() => 42;

        public string? LastFocusedId { get; private set; }
        public string? LastCurrentId { get; private set; }
        public string? LastSnapWindowId { get; private set; }
        public SnapDirection? LastSnapDir { get; private set; }

        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastFocusedId = id;
            if (string.IsNullOrEmpty(id)) return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No ID");
            if (!snap.Any(w => w.Id == id)) return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not found");
            return FocusResult;
        }

        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not found");
            return CurrentResult;
        }

        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not found");
            return CurrentResult;
        }

        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastCurrentId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not found");
            return CurrentResult;
        }

        public ExecutionResult Snap(SnapDirection dir, string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastSnapDir = dir;
            LastSnapWindowId = id;
            if (!string.IsNullOrEmpty(id) && !snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not found");
            return SnapResult;
        }
    }

    private sealed class TrackingMediaService : IMediaService
    {
        public MediaOperation? LastOp { get; private set; }
        public ExecutionResult Send(MediaOperation op) { LastOp = op; return ExecutionResult.Ok(op.ToString()); }
    }

    private sealed class TrackingVolumeService : IVolumeService
    {
        public int? LastSetPct { get; private set; }
        public VolumeDirection? LastDir { get; private set; }
        public int? LastAmount { get; private set; }

        public ExecutionResult SetVolume(int pct) { LastSetPct = pct; return ExecutionResult.Ok("ok"); }
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null)
        {
            LastDir = dir;
            LastAmount = amount;
            return ExecutionResult.Ok("ok");
        }
    }

    private sealed class TrackingMoveService : IWindowMoveService
    {
        public long? LastTargetHMonitor { get; private set; }
        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo target, DisplayTopology topo)
        {
            LastTargetHMonitor = target.HMonitor;
            return ExecutionResult.Ok("moved");
        }
    }

    private sealed class TrackingTopologyService : IDisplayTopologyService
    {
        private readonly MonitorInfo _currentMonitor;
        private readonly DisplayTopology _topology;

        public TrackingTopologyService(MonitorInfo current, DisplayTopology topology)
        {
            _currentMonitor = current;
            _topology = topology;
        }

        public DisplayTopology CaptureTopology() => _topology;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topo) => _currentMonitor;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topo) => _currentMonitor;
    }

    private sealed class NoOpMoveService : IWindowMoveService
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
