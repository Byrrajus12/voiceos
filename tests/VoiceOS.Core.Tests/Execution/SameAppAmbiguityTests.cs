using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// M6C: Same-app multi-window targeting.
/// Named-app window ops with a single match resolve; with multiple matches they fail
/// with TargetResolutionFailed — never arbitrary first-match selection.
/// </summary>
public class SameAppAmbiguityTests
{
    // ── One Chrome window → named Chrome resolves ─────────────────────────────

    [Fact]
    public async Task AppTarget_SingleChromeWindow_Resolves()
    {
        var chromeWindow = MakeWindow("w0", "chrome", hwnd: 10);
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };

        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(
            program, [chromeWindow]);

        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    // ── Two Chrome windows with same process → ambiguous ──────────────────────

    [Fact]
    public async Task AppTarget_TwoChromeWindows_IsAmbiguous()
    {
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "chrome", hwnd: 10),
            MakeWindow("w1", "chrome", hwnd: 20)
        };
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };

        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(windows.LastCurrentId);  // no action was taken
    }

    [Fact]
    public async Task AppTarget_TwoChromeWindows_FocusIsAmbiguous()
    {
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "chrome", hwnd: 10),
            MakeWindow("w1", "chrome", hwnd: 20)
        };
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("focused") };

        var program = new VoiceProgram([
            new FocusWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(windows.LastFocusedId);
    }

    [Fact]
    public async Task AppTarget_TwoChromeWindows_CloseIsAmbiguous()
    {
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "chrome", hwnd: 10),
            MakeWindow("w1", "chrome", hwnd: 20)
        };
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("closed") };

        var program = new VoiceProgram([
            new CloseWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
    }

    // ── Chrome + YouTube Music PWA: AUMID protects against false ambiguity ────

    [Fact]
    public async Task AppTarget_ChromeAndYouTubePwa_NotAmbiguous_AumidDistinguishes()
    {
        // Chrome has a specific AUMID; YouTube Music PWA has a different one.
        // AppWindowMatcher with Chrome's AUMID should only match the Chrome window.
        var chromeWindow = new WindowCandidate("w0", "chrome", "Gmail - Chrome",
            Hwnd: 10, AppUserModelId: "Google.Chrome_xyz");
        var ytmPwaWindow = new WindowCandidate("w1", "chrome", "YouTube Music - Chrome",
            Hwnd: 20, AppUserModelId: "Google.ChromeAppRuntime_abc");

        var catalog = new StubCatalog()
            .Add("chrome", "chrome", aumid: "Google.Chrome_xyz");

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };

        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows, catalog: catalog).ExecuteAsync(
            program, [chromeWindow, ytmPwaWindow]);

        // Chrome AUMID matches only chromeWindow; ytmPwaWindow has a different AUMID → no false ambiguity
        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    // ── CandidateWindowTarget exact HWND semantics preserved ─────────────────

    [Fact]
    public async Task CandidateWindowTarget_ExactHwnd_AlwaysResolvesExactly()
    {
        // Planning-time exact ID always resolves to that specific window, no ambiguity check.
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "chrome", hwnd: 10),
            MakeWindow("w1", "chrome", hwnd: 20)
        };
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };

        // CandidateWindowTarget selects w0 by exact ID — this is the case where planning
        // already uniquely identified the window (e.g. user said "the Gmail Chrome window").
        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new CandidateWindowTarget("w0"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("w0", windows.LastCurrentId);
    }

    // ── StepResultTarget exact HWND semantics preserved ──────────────────────

    [Fact]
    public async Task StepResultTarget_WithOtherChromeInSnapshot_ResolvesCorrectly()
    {
        // Even when another Chrome window exists, StepResultTarget resolves to the exact
        // window produced by step 1 — no false ambiguity.
        var existingChrome = MakeWindow("w0", "chrome", hwnd: 10);
        var newChrome = MakeWindow("w1", "chrome", hwnd: 20);

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(newChrome, "Focused"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var snapshot = new List<WindowCandidate> { existingChrome };

        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("chrome")),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Left)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("result:s1", windows.LastSnapWindowId);
        Assert.Equal(20, windows.LastSnapHwnd);  // new Chrome HWND, not existing
    }

    // ── Stale named-target remains a stale failure ────────────────────────────

    [Fact]
    public async Task AppTarget_SingleWindowStale_ReturnsExecutionFailed()
    {
        // Single Chrome window exists but its HWND is gone (stale).
        var staleWindow = new WindowCandidate("w0", "chrome", "Chrome", Hwnd: 10);
        var windows = new StubWindowService
        {
            CurrentResult = ExecutionResult.Fail(ExecutionStatus.WindowStale, "stale HWND")
        };

        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, [staleWindow]);

        // Resolved to single window, then execution service returned stale
        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
    }

    // ── No first-match fallback when target is not found ─────────────────────

    [Fact]
    public async Task AppTarget_NoChromeWindows_ReturnsResolutionFailed_NotForeground()
    {
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "notepad", hwnd: 99)
        };
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };

        var program = new VoiceProgram([
            new MinimizeWindowStep("s1", new AppTarget("chrome"))
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, snapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(windows.LastCurrentId);  // no foreground fallback
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WindowCandidate MakeWindow(string id, string proc, nint hwnd)
        => new(id, proc, $"{proc} - Window", Hwnd: hwnd);

    private static ProgramExecutor Build(
        StubLauncher? launcher = null,
        StubWindowService? windows = null,
        StubCatalog? catalog = null)
        => new(
            launcher ?? new StubLauncher(),
            catalog ?? DefaultCatalog(),
            windows ?? new StubWindowService(),
            new StubMediaService(),
            new StubVolumeService(),
            new NoOpWindowMoveService(),
            new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

    private static StubCatalog DefaultCatalog() => new StubCatalog()
        .Add("chrome", "chrome");

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

        public void SetFocusOrLaunch(string id, AppWindowResult r) => _focusOrLaunch[id] = r;

        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget target, IReadOnlyList<WindowCandidate> snapshot, CancellationToken ct = default)
            => Task.FromResult(
                _focusOrLaunch.TryGetValue(target.AppCandidateId, out var r) ? r
                    : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{target.AppCandidateId}' not configured"));

        public Task<AppWindowResult> LaunchNewAsync(AppTarget target, CancellationToken ct = default)
            => Task.FromResult(AppWindowResult.Fail(ExecutionStatus.AppNotFound, "not configured"));
    }

    private sealed class StubWindowService : IWindowService
    {
        public ExecutionResult FocusResult { get; set; } = ExecutionResult.Ok("focused");
        public ExecutionResult CurrentResult { get; set; } = ExecutionResult.Ok("done");
        public ExecutionResult SnapResult { get; set; } = ExecutionResult.Ok("snapped");
        public nint GetForegroundWindowHwnd() => 0;

        public string? LastFocusedId { get; private set; }
        public string? LastCurrentId { get; private set; }
        public string? LastSnapWindowId { get; private set; }
        public nint? LastSnapHwnd { get; private set; }

        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastFocusedId = id;
            if (string.IsNullOrEmpty(id)) return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No ID");
            if (!snap.Any(w => w.Id == id)) return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return FocusResult;
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
            LastSnapWindowId = id;
            if (!string.IsNullOrEmpty(id))
            {
                var c = snap.FirstOrDefault(w => w.Id == id);
                if (c == null) return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
                LastSnapHwnd = c.Hwnd;
            }
            return SnapResult;
        }
    }

    private sealed class StubMediaService : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok(op.ToString());
    }

    private sealed class StubVolumeService : IVolumeService
    {
        public ExecutionResult SetVolume(int pct) => ExecutionResult.Ok("ok");
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null) => ExecutionResult.Ok("ok");
    }

    private sealed class NoOpWindowMoveService : IWindowMoveService
    {
        public ExecutionResult MoveToMonitor(nint hwnd, MonitorInfo target, DisplayTopology topo)
            => ExecutionResult.Fail(ExecutionStatus.PlatformError, "NoOp");
    }

    private sealed class NoOpTopologyService : IDisplayTopologyService
    {
        public DisplayTopology CaptureTopology() => DisplayTopology.Empty;
        public MonitorInfo? GetCurrentMonitor(nint hwnd, DisplayTopology topo) => null;
        public MonitorInfo? GetForegroundMonitor(DisplayTopology topo) => null;
    }
}
