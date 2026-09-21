using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Monitors;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Tests ProgramExecutor: typed VoiceStep dispatch, step-result references,
/// sequential failure semantics, ordering, and launch result acquisition abstractions.
/// All tests are deterministic — no sleeping, no real Windows calls.
/// </summary>
public class ProgramExecutorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WindowCandidate MakeWindow(string id, string proc, nint hwnd = 99)
        => new(id, proc, $"{proc} - Window", Hwnd: hwnd);

    private static IReadOnlyList<WindowCandidate> EmptySnapshot => [];

    // Logical AppTargets — no process/AUMID on the target; catalog owns that
    private static AppTarget Chrome => new("chrome");
    private static AppTarget Terminal => new("windows-terminal");
    private static AppTarget Discord => new("discord");
    private static AppTarget VsCode => new("visual-studio-code");

    // Default catalog used by Build() — covers all apps referenced in tests
    private static StubCatalog DefaultCatalog() => new StubCatalog()
        .Add("chrome", "chrome")
        .Add("windows-terminal", "WindowsTerminal")
        .Add("discord", "Discord")
        .Add("visual-studio-code", "Code");

    // ── Single-step compatibility ─────────────────────────────────────────────

    [Fact]
    public async Task SingleStep_OpenApp_FocusOrLaunch_NoWindow_Launches()
    {
        var chromeWindow = MakeWindow("w-chrome", "chrome");
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWindow, "Launched"));

        var program = new VoiceProgram([new OpenAppStep("s1", Chrome)]);
        var result = await Build(launcher).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);
        Assert.Equal("s1", result.StepResults[0].StepId);
        Assert.Equal(chromeWindow, result.StepResults[0].ResultWindow);
    }

    [Fact]
    public async Task SingleStep_MediaControl_ReturnsSuccess()
    {
        var media = new StubMediaService { Result = ExecutionResult.Ok("ok") };
        var program = new VoiceProgram([new MediaControlStep("s1", MediaOperation.Play)]);
        var result = await Build(media: media).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(MediaOperation.Play, media.LastOp);
    }

    [Fact]
    public async Task SingleStep_SetVolume_ReturnsSuccess()
    {
        var vol = new StubVolumeService { SetResult = ExecutionResult.Ok("50%") };
        var program = new VoiceProgram([new SetVolumeStep("s1", 50)]);
        var result = await Build(volume: vol).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(50, vol.LastSetPercent);
    }

    [Fact]
    public async Task SingleStep_AdjustVolume_ReturnsSuccess()
    {
        var vol = new StubVolumeService { AdjustResult = ExecutionResult.Ok("up") };
        var program = new VoiceProgram([new AdjustVolumeStep("s1", VolumeDirection.Up)]);
        var result = await Build(volume: vol).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(VolumeDirection.Up, vol.LastAdjustDirection);
    }

    [Fact]
    public async Task SingleStep_MinimizeWindow_CurrentTarget_RoutesToForeground()
    {
        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };
        var program = new VoiceProgram([new MinimizeWindowStep("s1", new CurrentWindowTarget())]);
        var result = await Build(windows: windows).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Null(windows.LastCurrentId);  // null = foreground
    }

    [Fact]
    public async Task SingleStep_SnapWindow_CurrentTarget_ReturnsSuccess()
    {
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var program = new VoiceProgram([new SnapWindowStep("s1", new CurrentWindowTarget(), SnapDirection.Left)]);
        var result = await Build(windows: windows).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(SnapDirection.Left, windows.LastSnapDirection);
        Assert.Null(windows.LastSnapWindowId);
    }

    // ── Explicit two-step program ─────────────────────────────────────────────

    [Fact]
    public async Task TwoStep_OpenChrome_MinimizeDiscordExplicit()
    {
        // OpenApp(Chrome), then MinimizeWindow(App(Discord)) — second step is independent
        var chromeWindow = MakeWindow("w-chrome", "chrome");
        var discordWindow = MakeWindow("w-discord", "Discord", hwnd: 200);

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWindow));

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("minimized") };
        var snapshot = new List<WindowCandidate> { discordWindow };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome),
            new MinimizeWindowStep("s2", Discord)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.ExecutedCount);
        Assert.Equal("w-discord", windows.LastCurrentId);
    }

    // ── Step-result reference ─────────────────────────────────────────────────

    [Fact]
    public async Task StepResult_OpenTerminal_SnapResultRight()
    {
        // "Open Terminal and snap it right"
        // step2 snaps the exact window produced by step1 via the stable result ID
        var termWindow = MakeWindow("w-term", "WindowsTerminal", hwnd: 300);

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("windows-terminal", AppWindowResult.Ok(termWindow, "Launched"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Terminal),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.ExecutedCount);
        // StepResultTarget resolves via stable "result:s1" ID — never via snapshot-local candidate ID
        Assert.Equal("result:s1", windows.LastSnapWindowId);
        Assert.Equal(SnapDirection.Right, windows.LastSnapDirection);
    }

    [Fact]
    public async Task StepResult_SnapUsesStep1Window_NotForeground()
    {
        // Even if a different window is foreground, StepResultTarget resolves the produced window
        var launchedWindow = MakeWindow("w-launched", "WindowsTerminal", hwnd: 500);
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("windows-terminal", AppWindowResult.Ok(launchedWindow));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Terminal),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Left)
        ]);
        // Snapshot has an unrelated window so step2 is not using foreground
        var snapshot = new List<WindowCandidate> { MakeWindow("w-other", "notepad", hwnd: 1) };
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("result:s1", windows.LastSnapWindowId);
    }

    // ── StepResultTarget: candidate-ID collision regression ───────────────────

    [Fact]
    public async Task StepResultTarget_CandidateIdCollision_OperatesOnCorrectHwnd()
    {
        // Regression: initial snapshot has w0=VS Code HWND 100.
        // WindowAwareLauncher returns a new Chrome window also enumerated as w0 (HWND 300)
        // from a fresh snapshot call. StepResultTarget(s1) MUST snap HWND 300, not HWND 100.
        var vsCodeWindow = MakeWindow("w0", "Code", hwnd: 100);        // planning-time snapshot
        var newChromeWindow = MakeWindow("w0", "chrome", hwnd: 300);   // new snapshot after launch — same ID!

        var launcher = new StubLauncher();
        launcher.SetLaunchNew("chrome", AppWindowResult.Ok(newChromeWindow, "New Chrome"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var snapshot = new List<WindowCandidate> { vsCodeWindow, MakeWindow("w1", "Discord", hwnd: 200) };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome, AppActivationMode.NewInstance),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        // Must use stable "result:s1" — which resolves to HWND 300, not HWND 100
        Assert.Equal("result:s1", windows.LastSnapWindowId);
        // The entry the window service found must have HWND 300 (the new Chrome), never 100 (VS Code)
        Assert.Equal(300, windows.LastSnapHwnd);
        Assert.Equal(SnapDirection.Right, windows.LastSnapDirection);
    }

    [Fact]
    public async Task StepResultTarget_IdCollisionWithOldChrome_OperatesOnNewChrome()
    {
        // Pre-existing Chrome (w1, HWND 200) and new Chrome also enumerate as w1 (HWND 300).
        // StepResultTarget must operate on the new window (HWND 300).
        var oldChrome = MakeWindow("w1", "chrome", hwnd: 200);
        var newChrome = MakeWindow("w1", "chrome", hwnd: 300);  // same ID as old Chrome

        var launcher = new StubLauncher();
        launcher.SetLaunchNew("chrome", AppWindowResult.Ok(newChrome, "New Chrome"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "Code", hwnd: 100),
            oldChrome
        };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome, AppActivationMode.NewInstance),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("result:s1", windows.LastSnapWindowId);
        Assert.Equal(300, windows.LastSnapHwnd);  // new Chrome HWND, never old Chrome HWND
    }

    // ── Close prior result ────────────────────────────────────────────────────

    [Fact]
    public async Task StepResult_OpenChrome_CloseResult()
    {
        // "Open Chrome and then close it"
        var chromeWindow = MakeWindow("w-chrome", "chrome");

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWindow));

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("closed") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome),
            new CloseWindowStep("s2", new StepResultTarget("s1"))
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal("result:s1", windows.LastCurrentId);
    }

    // ── Independent explicit target ───────────────────────────────────────────

    [Fact]
    public async Task TwoStep_OpenChrome_SnapVSCodeLeft_Independent()
    {
        // "Open Chrome and snap VS Code left" — step2 targets VS Code independently
        var chromeWindow = MakeWindow("w-chrome", "chrome");
        var vsCodeWindow = MakeWindow("w-vscode", "Code", hwnd: 400);

        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("chrome", AppWindowResult.Ok(chromeWindow));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var snapshot = new List<WindowCandidate> { vsCodeWindow };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome),
            new SnapWindowStep("s2", VsCode, SnapDirection.Left)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        // step2 snapped VS Code, not Chrome's result
        Assert.Equal("w-vscode", windows.LastSnapWindowId);
        Assert.Equal(SnapDirection.Left, windows.LastSnapDirection);
    }

    // ── Failed result reference ───────────────────────────────────────────────

    [Fact]
    public async Task StepResult_NonWindowStep_TypedException_NeverForeground()
    {
        // MediaControl produces no ResultWindow; referencing it as StepResultTarget is a typed failure
        var media = new StubMediaService { Result = ExecutionResult.Ok("ok") };
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new MediaControlStep("s1", MediaOperation.Play),  // succeeds, no ResultWindow
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(windows: windows, media: media).ExecuteAsync(program, EmptySnapshot);

        Assert.Equal(2, result.ExecutedCount);
        Assert.True(result.StepResults[0].Succeeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[1].Status);
        // snap was never called — no foreground fallback
        Assert.Null(windows.LastSnapWindowId);
        Assert.Null(windows.LastSnapDirection);
    }

    [Fact]
    public async Task StepResult_MissingStep_TypedException()
    {
        // StepResultTarget references a step that doesn't exist in this program
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new SnapWindowStep("s1", new StepResultTarget("nonexistent"), SnapDirection.Right)
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, EmptySnapshot);

        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(windows.LastSnapWindowId);
    }

    // ── Failure stops program ─────────────────────────────────────────────────

    [Fact]
    public async Task Failure_FirstStep_SecondStepDoesNotExecute()
    {
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("bad-app",
            AppWindowResult.Fail(ExecutionStatus.AppNotFound, "not found"));

        var windows = new StubWindowService { CurrentResult = ExecutionResult.Ok("ok") };

        var catalog = DefaultCatalog().Add("bad-app", "bad");
        var program = new VoiceProgram([
            new OpenAppStep("s1", new AppTarget("bad-app")),
            new MinimizeWindowStep("s2", new CurrentWindowTarget())  // must not execute
        ]);
        var result = await Build(launcher, windows, catalog: catalog).ExecuteAsync(program, EmptySnapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);  // s2 was never run
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
        // s2 window service was never called
        Assert.Null(windows.LastCurrentId);
    }

    [Fact]
    public async Task Failure_MiddleStep_ThirdStepDoesNotExecute()
    {
        var windows = new StubWindowService
        {
            CurrentResult = ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "no window")
        };

        var executedOps = new List<string>();
        var trackingMedia = new TrackingMediaService(executedOps);

        // Step 1: media (succeeds), Step 2: close (fails — target not in snapshot), Step 3: media (must not execute)
        var program = new VoiceProgram([
            new MediaControlStep("s1", MediaOperation.Play),
            new CloseWindowStep("s2", new CandidateWindowTarget("nonexistent")),
            new MediaControlStep("s3", MediaOperation.Pause)  // must not execute
        ]);
        var result = await Build(windows: windows, media: trackingMedia).ExecuteAsync(program, EmptySnapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.ExecutedCount);
        Assert.True(result.StepResults[0].Succeeded);
        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[1].Status);
        Assert.Single(executedOps);
        Assert.Equal("Play", executedOps[0]);  // s3 Pause was never sent
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeStep_ExecutesInDeclaredOrder()
    {
        var order = new List<string>();
        var media = new OrderedMediaService(order);

        var program = new VoiceProgram([
            new MediaControlStep("s1", MediaOperation.Play),
            new MediaControlStep("s2", MediaOperation.Next),
            new MediaControlStep("s3", MediaOperation.Pause)
        ]);
        var result = await Build(media: media).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(3, result.ExecutedCount);
        Assert.Equal(["s1:Play", "s2:Next", "s3:Pause"], order);
    }

    // ── Launch result acquisition (via abstraction) ───────────────────────────

    [Fact]
    public async Task LaunchResult_ResultWindowIsRecordedAndAccessibleToLaterStep()
    {
        var launchedWindow = MakeWindow("w-new-term", "WindowsTerminal", hwnd: 777);
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("windows-terminal", AppWindowResult.Ok(launchedWindow, "Launched"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Terminal),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        // step1 recorded the launched window (original candidate ID preserved on ResultWindow)
        Assert.Equal(launchedWindow, result.StepResults[0].ResultWindow);
        // step2 resolved via stable "result:s1" ID — not the snapshot-local candidate ID
        Assert.Equal("result:s1", windows.LastSnapWindowId);
    }

    [Fact]
    public async Task LaunchResult_TimeoutFailure_TypedException_NeverForeground()
    {
        // Simulates the bounded-timeout case: launcher returns WindowNotFound
        var launcher = new StubLauncher();
        launcher.SetFocusOrLaunch("windows-terminal",
            AppWindowResult.Fail(ExecutionStatus.WindowNotFound, "Window did not appear within 8s"));

        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Terminal),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var result = await Build(launcher, windows).ExecuteAsync(program, EmptySnapshot);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.ExecutedCount);  // s2 never executed
        Assert.Equal(VoiceStepStatus.ExecutionFailed, result.StepResults[0].Status);
        // snap was never called — no foreground fallback
        Assert.Null(windows.LastSnapWindowId);
    }

    [Fact]
    public async Task LaunchResult_NewInstance_CallsLaunchNew()
    {
        var launchedWindow = MakeWindow("w-chrome-2", "chrome", hwnd: 888);
        var launcher = new StubLauncher();
        launcher.SetLaunchNew("chrome", AppWindowResult.Ok(launchedWindow));

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome, AppActivationMode.NewInstance)
        ]);
        var result = await Build(launcher).ExecuteAsync(program, EmptySnapshot);

        Assert.True(result.AllSucceeded);
        Assert.Equal(launchedWindow, result.StepResults[0].ResultWindow);
        Assert.True(launcher.LaunchNewWasCalled);
        Assert.False(launcher.FocusOrLaunchWasCalled);
    }

    [Fact]
    public async Task LaunchResult_NewInstance_ReturnsNewWindow_NotPreExisting()
    {
        // Chrome window A exists already. NewInstance should return the new window B, not A.
        // The launcher stub simulates correct behavior: LaunchNew returns the NEW window.
        var existingChromeWindow = MakeWindow("w-chrome-a", "chrome", hwnd: 100);
        var newChromeWindow = MakeWindow("w-chrome-b", "chrome", hwnd: 200);

        var launcher = new StubLauncher();
        launcher.SetLaunchNew("chrome", AppWindowResult.Ok(newChromeWindow, "Launched new instance"));

        var snapshot = new List<WindowCandidate> { existingChromeWindow };

        var program = new VoiceProgram([
            new OpenAppStep("s1", Chrome, AppActivationMode.NewInstance),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Right)
        ]);
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };
        var result = await Build(launcher, windows).ExecuteAsync(program, snapshot);

        Assert.True(result.AllSucceeded);
        // ResultWindow preserves the original candidate ID and correct HWND
        Assert.Equal("w-chrome-b", result.StepResults[0].ResultWindow?.Id);
        Assert.Equal(newChromeWindow.Hwnd, result.StepResults[0].ResultWindow?.Hwnd);
        // step2 resolved via stable "result:s1" — never via the snapshot-local candidate ID
        Assert.Equal("result:s1", windows.LastSnapWindowId);
    }

    // ── AppTarget catalog resolution ──────────────────────────────────────────

    [Fact]
    public async Task AppTarget_NotInCatalog_TypedException()
    {
        // AppTarget referencing an app not in the catalog → TargetResolutionFailed
        var windows = new StubWindowService { SnapResult = ExecutionResult.Ok("snapped") };

        var program = new VoiceProgram([
            new SnapWindowStep("s1", new AppTarget("unknown-app"), SnapDirection.Left)
        ]);
        var result = await Build(windows: windows).ExecuteAsync(program, EmptySnapshot);

        Assert.Equal(VoiceStepStatus.TargetResolutionFailed, result.StepResults[0].Status);
        Assert.Null(windows.LastSnapWindowId);
    }

    // ── AppWindowMatcher (unit tests for the extracted helper) ────────────────

    [Fact]
    public void Matcher_ProcessNameOnly_MatchesByProcess()
    {
        var snapshot = new List<WindowCandidate>
        {
            MakeWindow("w0", "chrome"),
            MakeWindow("w1", "discord")
        };
        var result = AppWindowMatcher.FindMatching("chrome", null, snapshot);
        Assert.Single(result);
        Assert.Equal("w0", result[0].Id);
    }

    [Fact]
    public void Matcher_AumidPresent_MatchesByAumidAndProcessFallback()
    {
        var w0 = new WindowCandidate("w0", "chrome", "Chrome", Hwnd: 1, AppUserModelId: "Google.Chrome_xyz");
        var w1 = MakeWindow("w1", "chrome");  // no AUMID — process-name fallback
        var snapshot = new List<WindowCandidate> { w0, w1 };

        var result = AppWindowMatcher.FindMatching("chrome", "Google.Chrome_xyz", snapshot);

        Assert.Equal(2, result.Count);  // w0 (aumid match) + w1 (process fallback)
    }

    [Fact]
    public void Matcher_AumidMismatch_HardRejects()
    {
        var w = new WindowCandidate("w0", "chrome", "Chrome", Hwnd: 1, AppUserModelId: "Some.Other.App");
        var snapshot = new List<WindowCandidate> { w };

        var result = AppWindowMatcher.FindMatching("chrome", "Google.Chrome_xyz", snapshot);

        Assert.Empty(result);  // AUMID mismatch is hard reject
    }

    [Fact]
    public void Matcher_NoProcessOrAumid_ReturnsEmpty()
    {
        var snapshot = new List<WindowCandidate> { MakeWindow("w0", "chrome") };
        var result = AppWindowMatcher.FindMatching(null, null, snapshot);
        Assert.Empty(result);
    }

    // ── Builder / stubs ───────────────────────────────────────────────────────

    private static ProgramExecutor Build(
        StubLauncher? launcher = null,
        StubWindowService? windows = null,
        IMediaService? media = null,
        StubVolumeService? volume = null,
        StubCatalog? catalog = null,
        IWindowMoveService? windowMover = null,
        IDisplayTopologyService? topoService = null)
        => new(
            launcher ?? new StubLauncher(),
            catalog ?? DefaultCatalog(),
            windows ?? new StubWindowService(),
            media ?? new StubMediaService(),
            volume ?? new StubVolumeService(),
            windowMover ?? new NoOpWindowMoveService(),
            topoService ?? new NoOpTopologyService(),
            NullLogger<ProgramExecutor>.Instance);

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

        public bool FocusOrLaunchWasCalled { get; private set; }
        public bool LaunchNewWasCalled { get; private set; }

        public void SetFocusOrLaunch(string appId, AppWindowResult result) => _focusOrLaunch[appId] = result;
        public void SetLaunchNew(string appId, AppWindowResult result) => _launchNew[appId] = result;

        public Task<AppWindowResult> FocusOrLaunchAsync(AppTarget target, IReadOnlyList<WindowCandidate> snapshot, CancellationToken ct = default)
        {
            FocusOrLaunchWasCalled = true;
            return Task.FromResult(
                _focusOrLaunch.TryGetValue(target.AppCandidateId, out var r) ? r
                    : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{target.AppCandidateId}' not configured"));
        }

        public Task<AppWindowResult> LaunchNewAsync(AppTarget target, CancellationToken ct = default)
        {
            LaunchNewWasCalled = true;
            return Task.FromResult(
                _launchNew.TryGetValue(target.AppCandidateId, out var r) ? r
                    : AppWindowResult.Fail(ExecutionStatus.AppNotFound, $"'{target.AppCandidateId}' not configured"));
        }
    }

    private sealed class StubWindowService : IWindowService
    {
        public ExecutionResult FocusResult { get; set; } = ExecutionResult.Ok("focused");
        public ExecutionResult CurrentResult { get; set; } = ExecutionResult.Ok("done");
        public ExecutionResult SnapResult { get; set; } = ExecutionResult.Ok("snapped");
        public nint ForegroundHwnd { get; set; } = 0;
        public nint GetForegroundWindowHwnd() => ForegroundHwnd;

        public string? LastFocusedId { get; private set; }
        public string? LastCurrentId { get; private set; }
        public string? LastSnapWindowId { get; private set; }
        public SnapDirection? LastSnapDirection { get; private set; }
        public nint? LastSnapHwnd { get; private set; }

        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap)
        {
            LastFocusedId = id;
            if (string.IsNullOrEmpty(id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No ID");
            if (!snap.Any(w => w.Id == id))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
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
            LastSnapDirection = dir;
            LastSnapWindowId = id;
            if (!string.IsNullOrEmpty(id))
            {
                var candidate = snap.FirstOrDefault(w => w.Id == id);
                if (candidate == null)
                    return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
                LastSnapHwnd = candidate.Hwnd;
            }
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
        public ExecutionResult SetVolume(int pct) { LastSetPercent = pct; return SetResult; }
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null)
        {
            LastAdjustDirection = dir;
            LastAdjustAmount = amount;
            return AdjustResult;
        }
    }

    private sealed class TrackingMediaService : IMediaService
    {
        private readonly List<string> _log;
        public TrackingMediaService(List<string> log) => _log = log;
        public ExecutionResult Send(MediaOperation op) { _log.Add(op.ToString()); return ExecutionResult.Ok(op.ToString()); }
    }

    private sealed class OrderedMediaService : IMediaService
    {
        private readonly List<string> _order;
        private int _idx;
        private readonly string[] _ids = ["s1", "s2", "s3"];
        public OrderedMediaService(List<string> order) => _order = order;
        public ExecutionResult Send(MediaOperation op)
        {
            var id = _idx < _ids.Length ? _ids[_idx++] : "sX";
            _order.Add($"{id}:{op}");
            return ExecutionResult.Ok(op.ToString());
        }
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
