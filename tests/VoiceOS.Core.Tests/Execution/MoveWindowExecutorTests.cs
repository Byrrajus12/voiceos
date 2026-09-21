using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Tests ProgramExecutor.ExecuteMoveWindow dispatch:
/// target resolution, monitor resolution outcomes, and sequential failure semantics.
/// No real Win32 calls — all services are fakes.
/// </summary>
public class MoveWindowExecutorTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static WindowCandidate MakeWindow(string id, string proc, nint hwnd)
        => new(id, proc, $"{proc} - Window", Hwnd: hwnd);

    // Two-monitor topology: left internal + right external (primary)
    private static readonly MonitorInfo LeftMonitor = new(
        HMonitor: 1001,
        Bounds:   new MonitorBounds(-2560, 0, 0, 1440),
        WorkArea: new MonitorBounds(-2560, 0, 0, 1440),
        IsPrimary: false,
        GdiDeviceName: "\\\\.\\DISPLAY1",
        DevicePath: null,
        FriendlyName: "Built-in Display",
        ConnectionKind: DisplayConnectionKind.Internal);

    private static readonly MonitorInfo RightMonitor = new(
        HMonitor: 1002,
        Bounds:   new MonitorBounds(0, 0, 1920, 1080),
        WorkArea: new MonitorBounds(0, 0, 1920, 1040),
        IsPrimary: true,
        GdiDeviceName: "\\\\.\\DISPLAY2",
        DevicePath: null,
        FriendlyName: "MSI G241",
        ConnectionKind: DisplayConnectionKind.External);

    private static readonly DisplayTopology TwoMonitorTopology =
        new([LeftMonitor, RightMonitor]);

    private static StubCatalog DefaultCatalog() => new StubCatalog()
        .Add("chrome", "chrome")
        .Add("discord", "Discord");

    // ── Target resolution: named AppTarget ───────────────────────────────────

    [Fact]
    public async Task MoveWindow_AppTarget_ResolvesWindow_CallsMover()
    {
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var snapshot = new List<WindowCandidate> { chromeWin };

        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step = new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]), snapshot, TwoMonitorTopology);

        Assert.True(result.AllSucceeded, result.FirstFailure?.Message);
        Assert.Equal(100, mover.LastHwnd);                      // chrome's HWND
        Assert.Equal(LeftMonitor.HMonitor, mover.LastTarget?.HMonitor);  // "other" from right = left
    }

    [Fact]
    public async Task MoveWindow_AppTarget_ReturnsResultWindow_WithOriginalHwnd()
    {
        // ResultWindow must carry the original HWND so StepResultTarget lookups work
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 200);
        var snapshot = new List<WindowCandidate> { chromeWin };

        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step = new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]), snapshot, TwoMonitorTopology);

        Assert.Equal(200, result.StepResults[0].ResultWindow?.Hwnd);
    }

    // ── Target resolution: CurrentWindowTarget ────────────────────────────────

    [Fact]
    public async Task MoveWindow_CurrentWindowTarget_PassesConcreteHwndToMover()
    {
        // Executor must sample foreground HWND once and pass it directly — never hwnd=0
        const nint ForegroundHwnd = 555;
        var mover   = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var windows = new FixedForegroundWindowService(ForegroundHwnd);
        // currentMonitor for the sampled HWND = RightMonitor → "other" = LeftMonitor
        var topo = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step = new MoveWindowStep("s1", new CurrentWindowTarget(), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo, windows: windows)
            .ExecuteAsync(new VoiceProgram([step]), [], TwoMonitorTopology);

        Assert.True(result.AllSucceeded, result.FirstFailure?.Message);
        Assert.Equal(ForegroundHwnd, mover.LastHwnd);   // concrete HWND, never 0
        Assert.Equal(LeftMonitor.HMonitor, mover.LastTarget?.HMonitor);
    }

    [Fact]
    public async Task MoveWindow_CurrentWindowTarget_SameHwndUsedForMonitorResolutionAndMovement()
    {
        // Regression: foreground HWND must be sampled exactly once and used for BOTH
        // current-monitor resolution and movement, preventing a TOCTOU race where
        // monitor is resolved for window A but window B is actually moved.
        const nint ForegroundHwnd = 888;
        var mover   = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var windows = new FixedForegroundWindowService(ForegroundHwnd);
        var topo    = new CapturingTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step = new MoveWindowStep("s1", new CurrentWindowTarget(), new OtherMonitor());
        await Build(mover: mover, topo: topo, windows: windows)
            .ExecuteAsync(new VoiceProgram([step]), [], TwoMonitorTopology);

        // Both GetCurrentMonitor and MoveToMonitor received the exact same HWND
        Assert.Equal(ForegroundHwnd, topo.LastGetCurrentMonitorHwnd);
        Assert.Equal(ForegroundHwnd, mover.LastHwnd);
    }

    [Fact]
    public async Task MoveWindow_CurrentWindowTarget_NoForegroundWindow_ReturnsResolutionFailed()
    {
        // If GetForegroundWindowHwnd returns 0, the step must fail cleanly
        var mover   = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var windows = new FixedForegroundWindowService(hwnd: 0);
        var topo    = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step = new MoveWindowStep("s1", new CurrentWindowTarget(), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo, windows: windows)
            .ExecuteAsync(new VoiceProgram([step]), [], TwoMonitorTopology);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(mover.LastHwnd);
    }

    [Fact]
    public async Task MoveWindow_CurrentWindowTarget_PrimaryTarget_UsesPrimaryMonitor()
    {
        const nint ForegroundHwnd = 42;
        var mover   = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var windows = new FixedForegroundWindowService(ForegroundHwnd);
        // window currently on LeftMonitor; Primary is RightMonitor
        var topo = new FixedTopologyService(TwoMonitorTopology, currentMonitor: LeftMonitor);

        var step = new MoveWindowStep("s1", new CurrentWindowTarget(), new PrimaryMonitor());
        var result = await Build(mover: mover, topo: topo, windows: windows)
            .ExecuteAsync(new VoiceProgram([step]), [], TwoMonitorTopology);

        Assert.True(result.AllSucceeded, result.FirstFailure?.Message);
        Assert.Equal(RightMonitor.HMonitor, mover.LastTarget?.HMonitor);
    }

    // ── Target resolution: StepResultTarget ──────────────────────────────────

    [Fact]
    public async Task MoveWindow_StepResultTarget_UsesExactHwndFromPriorStep()
    {
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 777);
        var launcher  = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWin));

        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo  = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("chrome")),
            new MoveWindowStep("s2", new StepResultTarget("s1"), new OtherMonitor())
        ]);
        var result = await Build(launcher: launcher, mover: mover, topo: topo)
            .ExecuteAsync(program, [], TwoMonitorTopology);

        Assert.True(result.AllSucceeded, result.FirstFailure?.Message);
        Assert.Equal(777, mover.LastHwnd);   // exact HWND from OpenApp result
    }

    // ── Monitor resolution failures ───────────────────────────────────────────

    [Fact]
    public async Task MoveWindow_MonitorNotFound_ReturnsResolutionFailed_NoMove()
    {
        // OtherMonitor on a single-monitor topology → not found
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var singleTopo = new DisplayTopology([RightMonitor]);

        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo  = new FixedTopologyService(singleTopo, currentMonitor: RightMonitor);

        var step   = new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]),
                new List<WindowCandidate> { chromeWin }, singleTopo);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(mover.LastHwnd);   // mover was never called
    }

    [Fact]
    public async Task MoveWindow_MonitorAmbiguous_ReturnsResolutionFailed_NoMove()
    {
        // InternalMonitor on a topology with two internal monitors → ambiguous
        var internal1 = LeftMonitor with { HMonitor = 2001, ConnectionKind = DisplayConnectionKind.Internal };
        var internal2 = RightMonitor with { HMonitor = 2002, ConnectionKind = DisplayConnectionKind.Internal, IsPrimary = false };
        var ambiguousTopo = new DisplayTopology([internal1, internal2]);

        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo  = new FixedTopologyService(ambiguousTopo, currentMonitor: internal1);

        var step   = new MoveWindowStep("s1", new AppTarget("chrome"), new InternalMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]),
                new List<WindowCandidate> { chromeWin }, ambiguousTopo);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(mover.LastHwnd);
    }

    [Fact]
    public async Task MoveWindow_NoCurrentContext_ReturnsResolutionFailed_NoMove()
    {
        // OtherMonitor needs current monitor context; topology service returns null
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo  = new FixedTopologyService(TwoMonitorTopology, currentMonitor: null);

        var step   = new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]),
                new List<WindowCandidate> { chromeWin }, TwoMonitorTopology);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(mover.LastHwnd);
    }

    // ── TopologyStale from mover ──────────────────────────────────────────────

    [Fact]
    public async Task MoveWindow_TopologyStale_ReturnsExecutionFailed()
    {
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var mover = new CapturingWindowMoveService(
            ExecutionResult.Fail(ExecutionStatus.TopologyStale, "Monitor disconnected"));
        var topo = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step   = new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]),
                new List<WindowCandidate> { chromeWin }, TwoMonitorTopology);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
    }

    // ── Movement hard failure stops compound program ──────────────────────────

    [Fact]
    public async Task MoveWindow_HardFailure_StopsCompoundProgram()
    {
        // Step 1: MoveWindow (fails), Step 2: MediaControl (must not execute)
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 100);
        var mover = new CapturingWindowMoveService(
            ExecutionResult.Fail(ExecutionStatus.PlatformError, "Win32 error"));
        var topo  = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);
        var media = new TrackingMediaService();

        var program = new VoiceProgram([
            new MoveWindowStep("s1", new AppTarget("chrome"), new OtherMonitor()),
            new MediaControlStep("s2", MediaOperation.Play)   // must not execute
        ]);
        var result = await Build(mover: mover, topo: topo, media: media)
            .ExecuteAsync(program, new List<WindowCandidate> { chromeWin }, TwoMonitorTopology);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);   // s2 never ran
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
        Assert.Empty(media.Ops);
    }

    // ── Window target resolution failure ──────────────────────────────────────

    [Fact]
    public async Task MoveWindow_WindowNotFound_ReturnsResolutionFailed()
    {
        // AppTarget referencing an app with no window in snapshot
        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        var topo  = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var step   = new MoveWindowStep("s1", new AppTarget("discord"), new OtherMonitor());
        var result = await Build(mover: mover, topo: topo)
            .ExecuteAsync(new VoiceProgram([step]), [], TwoMonitorTopology);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(mover.LastHwnd);
    }

    // ── OpenChrome + MoveToOtherMonitor compound ──────────────────────────────

    [Fact]
    public async Task TwoStep_OpenChrome_MoveToOther_CompoundSucceeds()
    {
        var chromeWin = MakeWindow("w-chrome", "chrome", hwnd: 300);
        var launcher  = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWin));

        var mover = new CapturingWindowMoveService(ExecutionResult.Ok("moved"));
        // After open, chrome is on RightMonitor; OtherMonitor = LeftMonitor
        var topo  = new FixedTopologyService(TwoMonitorTopology, currentMonitor: RightMonitor);

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("chrome")),
            new MoveWindowStep("s2", new StepResultTarget("s1"), new OtherMonitor())
        ]);
        var result = await Build(launcher: launcher, mover: mover, topo: topo)
            .ExecuteAsync(program, [], TwoMonitorTopology);

        Assert.True(result.AllSucceeded, result.FirstFailure?.Message);
        Assert.Equal(2, result.ExecutedCount);
        Assert.Equal(300, mover.LastHwnd);
        Assert.Equal(LeftMonitor.HMonitor, mover.LastTarget?.HMonitor);
        // ResultWindow for s2 preserves the chrome HWND
        Assert.Equal(300, result.StepResults[1].ResultWindow?.Hwnd);
    }

    // ── Builder / stubs ───────────────────────────────────────────────────────

    private static ProgramExecutor Build(
        StubLauncher? launcher = null,
        IWindowMoveService? mover = null,
        IDisplayTopologyService? topo = null,
        IMediaService? media = null,
        IWindowService? windows = null)
        => new(
            launcher ?? new StubLauncher(),
            DefaultCatalog(),
            windows ?? new NoOpWindowService(),
            media ?? new NoOpMediaService(),
            new NoOpVolumeService(),
            mover ?? new NoOpWindowMoveService(),
            topo  ?? new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

    // ── Stubs ─────────────────────────────────────────────────────────────────

    private sealed class StubCatalog : IAppCatalog
    {
        private readonly Dictionary<string, AppEntry> _e = new(StringComparer.OrdinalIgnoreCase);
        public StubCatalog Add(string id, string proc)
        {
            _e[id] = new AppEntry(id, id, proc, AppLaunchKind.Win32, id, null, null);
            return this;
        }
        public IReadOnlyList<AppEntry> GetAll() => [.. _e.Values];
        public AppEntry? FindById(string id) => _e.GetValueOrDefault(id);
    }

    private sealed class StubLauncher : IWindowAwareLauncher
    {
        private readonly Dictionary<string, AppWindowResult> _fol = new(StringComparer.OrdinalIgnoreCase);
        public void SetFocusOrLaunch(string id, AppWindowResult r) => _fol[id] = r;
        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget t, IReadOnlyList<WindowCandidate> s, CancellationToken ct = default)
            => Task.FromResult(_fol.TryGetValue(t.AppCandidateId, out var r) ? r
                : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{t.AppCandidateId}' not configured"));
        public Task<AppWindowResult> LaunchNewAsync(AppTarget t, CancellationToken ct = default)
            => Task.FromResult(AppWindowResult.Fail(ExecutionStatus.AppNotFound, "not configured"));
    }

    /// <summary>Captures the last MoveToMonitor call for assertions.</summary>
    private sealed class CapturingWindowMoveService : IWindowMoveService
    {
        private readonly ExecutionResult _result;
        public nint? LastHwnd { get; private set; }
        public MonitorInfo? LastTarget { get; private set; }

        public CapturingWindowMoveService(ExecutionResult result) => _result = result;

        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo targetMonitor, DisplayTopology topology)
        {
            LastHwnd   = hwnd;
            LastTarget = targetMonitor;
            return _result;
        }
    }

    /// <summary>Returns a fixed current monitor for any HWND.</summary>
    private sealed class FixedTopologyService : IDisplayTopologyService
    {
        private readonly DisplayTopology _topology;
        private readonly MonitorInfo? _currentMonitor;

        public FixedTopologyService(
            DisplayTopology topology,
            MonitorInfo? currentMonitor = null)
        {
            _topology       = topology;
            _currentMonitor = currentMonitor;
        }

        public DisplayTopology CaptureTopology() => _topology;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology) => _currentMonitor;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topology) => _currentMonitor;
    }

    /// <summary>Captures the HWND passed to GetCurrentMonitor for race-condition assertions.</summary>
    private sealed class CapturingTopologyService : IDisplayTopologyService
    {
        private readonly DisplayTopology _topology;
        private readonly MonitorInfo? _currentMonitor;

        public nint? LastGetCurrentMonitorHwnd { get; private set; }

        public CapturingTopologyService(DisplayTopology topology, MonitorInfo? currentMonitor)
        {
            _topology       = topology;
            _currentMonitor = currentMonitor;
        }

        public DisplayTopology CaptureTopology() => _topology;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology)
        {
            LastGetCurrentMonitorHwnd = hwnd;
            return _currentMonitor;
        }
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topology) => _currentMonitor;
    }

    /// <summary>Window service that returns a fixed foreground HWND.</summary>
    private sealed class FixedForegroundWindowService : IWindowService
    {
        private readonly nint _hwnd;
        public FixedForegroundWindowService(nint hwnd) => _hwnd = hwnd;
        public nint GetForegroundWindowHwnd() => _hwnd;
        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> s)    => ExecutionResult.Ok("ok");
        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> s)    => ExecutionResult.Ok("ok");
        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
        public ExecutionResult Snap(SnapDirection d, string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpWindowMoveService : IWindowMoveService
    {
        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo t, DisplayTopology topo)
            => ExecutionResult.Fail(ExecutionStatus.PlatformError, "NoOp");
    }

    private sealed class NoOpTopologyService : IDisplayTopologyService
    {
        public DisplayTopology CaptureTopology() => DisplayTopology.Empty;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topology) => null;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topology) => null;
    }

    private sealed class NoOpWindowService : IWindowService
    {
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> s)    => ExecutionResult.Ok("ok");
        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> s)    => ExecutionResult.Ok("ok");
        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
        public ExecutionResult Snap(SnapDirection d, string? id, IReadOnlyList<WindowCandidate> s) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpMediaService : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok(op.ToString());
    }

    private sealed class TrackingMediaService : IMediaService
    {
        public List<MediaOperation> Ops { get; } = [];
        public ExecutionResult Send(MediaOperation op) { Ops.Add(op); return ExecutionResult.Ok(op.ToString()); }
    }

    private sealed class NoOpVolumeService : IVolumeService
    {
        public ExecutionResult SetVolume(int pct) => ExecutionResult.Ok(pct.ToString());
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null) => ExecutionResult.Ok(dir.ToString());
    }
}
