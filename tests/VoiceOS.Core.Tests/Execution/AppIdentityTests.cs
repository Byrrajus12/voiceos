using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Tests for AUMID-based identity matching in FocusOrLaunch.
/// Covers Chrome/PWA distinction, fallback for no-AUMID apps, and strong-identity mismatch.
/// </summary>
public class AppIdentityTests
{
    private const string ChromeAumid = "Chrome";
    private const string YtMusicAumid = "Chrome._crx_cinhimbnkkghhklpknlkffjgod";
    private const string ChromeProcess = "chrome";

    // ── Chrome/PWA distinction ─────────────────────────────────────────────────

    [Fact]
    public async Task FocusOrLaunch_ChromeAumid_ResolvesOnlyChromeWindow_NotYouTubeMusic()
    {
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused Chrome") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "New Tab - Google Chrome", AppUserModelId: ChromeAumid),
            new("w1", ChromeProcess, "YouTube Music",            AppUserModelId: YtMusicAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "google-chrome",
            AppProcessName: ChromeProcess,
            AppUserModelId: ChromeAumid);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    [Fact]
    public async Task FocusOrLaunch_YtMusicAumid_ResolvesOnlyYouTubeMusic_NotChrome()
    {
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused YT Music") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "New Tab - Google Chrome", AppUserModelId: ChromeAumid),
            new("w1", ChromeProcess, "YouTube Music",            AppUserModelId: YtMusicAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "youtube-music",
            AppProcessName: ChromeProcess,
            AppUserModelId: YtMusicAumid);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w1", windows.LastFocusedId);
    }

    [Fact]
    public async Task FocusOrLaunch_ChromeAumid_YtMusicWindowOnly_Launches()
    {
        var launcher = new StubLauncher();
        launcher.SetResult("google-chrome", ExecutionResult.Ok("Launched Chrome"));

        // Only YouTube Music is open — Chrome is absent → should launch Chrome, not focus YT Music.
        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "YouTube Music", AppUserModelId: YtMusicAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "google-chrome",
            AppProcessName: ChromeProcess,
            AppUserModelId: ChromeAumid);

        var result = await Build(launcher: launcher).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("google-chrome", launcher.LastLaunchedId);
    }

    [Fact]
    public async Task FocusOrLaunch_YtMusicAumid_NoWindowOpen_Launches()
    {
        var launcher = new StubLauncher();
        launcher.SetResult("youtube-music", ExecutionResult.Ok("Launched YT Music"));

        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "New Tab - Google Chrome", AppUserModelId: ChromeAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "youtube-music",
            AppProcessName: ChromeProcess,
            AppUserModelId: YtMusicAumid);

        var result = await Build(launcher: launcher).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("youtube-music", launcher.LastLaunchedId);
    }

    // ── Fallback: no AUMID app ─────────────────────────────────────────────────

    [Fact]
    public async Task FocusOrLaunch_NoAumidApp_MatchesByProcessName()
    {
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused Code") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", "Code", "Program.cs - VoiceOS - Visual Studio Code"),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "visual-studio-code",
            AppProcessName: "Code",
            AppUserModelId: null);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    [Fact]
    public async Task FocusOrLaunch_NoAumidApp_WindowHasAumid_FallsBackToProcessName()
    {
        // App has no AUMID but window happens to have one — process name fallback still used.
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", "notepad", "Untitled - Notepad", AppUserModelId: "Microsoft.Notepad"),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "notepad",
            AppProcessName: "notepad",
            AppUserModelId: null);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    // ── Strong identity mismatch ───────────────────────────────────────────────

    [Fact]
    public async Task FocusOrLaunch_AppHasAumidA_WindowHasAumidB_SameProcess_MustNotMatch()
    {
        var launcher = new StubLauncher();
        launcher.SetResult("app-a", ExecutionResult.Ok("Launched A"));

        // Window has AUMID "B" while app requires AUMID "A" — same ProcessName should not cause a match.
        var snapshot = new List<WindowCandidate>
        {
            new("w0", "shared-proc", "Window B", AppUserModelId: "AppB"),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "app-a",
            AppProcessName: "shared-proc",
            AppUserModelId: "AppA");

        var result = await Build(launcher: launcher).ExecuteAsync(plan, snapshot);

        // No window matched → must launch, not focus.
        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("app-a", launcher.LastLaunchedId);
    }

    [Fact]
    public async Task FocusOrLaunch_AppHasAumid_WindowHasNoAumid_SameProcess_MatchesByFallback()
    {
        // VS Code case: shortcut has AUMID but the running window exposes no per-window AUMID.
        // Missing evidence is not contradictory — ProcessName fallback must still apply.
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", "Code", "Program.cs - VoiceOS - Visual Studio Code", AppUserModelId: null),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "visual-studio-code",
            AppProcessName: "Code",
            AppUserModelId: "Microsoft.VisualStudioCode");

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    // ── Explicit regression: VS Code measured scenario ────────────────────────

    [Fact]
    public async Task FocusOrLaunch_VsCode_ShortcutAumid_RuntimeNoAumid_ProcessNameCode_Focuses()
    {
        // Measured: VS Code shortcut AUMID = Microsoft.VisualStudioCode,
        // running window PerWin AUMID = (none), ProcessName = Code.
        // Must focus the running window, not attempt a launch.
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused VS Code") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", "Code", "src/VoiceOS.Core/Execution/PlanExecutor.cs — VoiceOS",
                AppUserModelId: null,
                ExecutablePath: @"C:\Users\HP\AppData\Local\Programs\Microsoft VS Code\Code.exe"),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "visual-studio-code",
            AppProcessName: "Code",
            AppUserModelId: "Microsoft.VisualStudioCode");

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    // ── Explicit regression: Chrome/YT Music conflicting AUMID hard reject ────

    [Fact]
    public async Task FocusOrLaunch_Chrome_ConflictingAumid_NeverFallsBackToProcessName()
    {
        // Both windows have AUMID. Chrome app asks for AUMID=Chrome.
        // YouTube Music window has AUMID=Chrome._crx_... → hard reject, no ProcessName fallback.
        // Chrome window has AUMID=Chrome → strong match.
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused Chrome") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "YouTube Music", AppUserModelId: YtMusicAumid),
            new("w1", ChromeProcess, "New Tab - Google Chrome", AppUserModelId: ChromeAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "google-chrome",
            AppProcessName: ChromeProcess,
            AppUserModelId: ChromeAumid);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w1", windows.LastFocusedId); // Chrome window focused, YT Music ignored
    }

    [Fact]
    public async Task FocusOrLaunch_MatchingAumid_Focuses()
    {
        var windows = new StubWindowService { FocusResult = ExecutionResult.Ok("Focused") };

        var snapshot = new List<WindowCandidate>
        {
            new("w0", ChromeProcess, "New Tab - Google Chrome", AppUserModelId: ChromeAumid),
        };

        var plan = new VoicePlan(VoiceAction.OpenApp,
            AppCandidateId: "google-chrome",
            AppProcessName: ChromeProcess,
            AppUserModelId: ChromeAumid);

        var result = await Build(windows: windows).ExecuteAsync(plan, snapshot);

        Assert.Equal(ExecutionStatus.Success, result.Status);
        Assert.Equal("w0", windows.LastFocusedId);
    }

    // ── Launch metadata preservation ───────────────────────────────────────────

    [Fact]
    public void AppEntry_StoresLaunchArgumentsAndAumid()
    {
        var entry = new AppEntry(
            "youtube-music",
            "YouTube Music",
            "chrome",
            AppLaunchKind.Win32,
            @"C:\Program Files\Google\Chrome\Application\chrome_proxy.exe",
            LaunchArguments: "--profile-directory=Default --app-id=cinhimbnkk",
            AppUserModelId: YtMusicAumid);

        Assert.Equal("--profile-directory=Default --app-id=cinhimbnkk", entry.LaunchArguments);
        Assert.Equal(YtMusicAumid, entry.AppUserModelId);
    }

    [Fact]
    public void AppEntry_LaunchArgumentsDefaultsToNull()
    {
        var entry = new AppEntry("notepad", "Notepad", "notepad", AppLaunchKind.Win32, "notepad.exe");
        Assert.Null(entry.LaunchArguments);
        Assert.Null(entry.AppUserModelId);
    }

    [Fact]
    public void AppCandidate_PropagatesAumid()
    {
        var candidate = new AppCandidate("youtube-music", "YouTube Music", "chrome", YtMusicAumid);
        Assert.Equal(YtMusicAumid, candidate.AppUserModelId);
    }

    [Fact]
    public void WindowCandidate_PropagatesAumid()
    {
        var window = new WindowCandidate("w0", "chrome", "YouTube Music", AppUserModelId: YtMusicAumid);
        Assert.Equal(YtMusicAumid, window.AppUserModelId);
    }

    [Fact]
    public void WindowCandidate_PropagatesExecutablePath()
    {
        const string exePath = @"C:\Users\HP\AppData\Local\Programs\Microsoft VS Code\Code.exe";
        var window = new WindowCandidate("w0", "Code", "VS Code", ExecutablePath: exePath);
        Assert.Equal(exePath, window.ExecutablePath);
    }

    [Fact]
    public void WindowCandidate_ExecutablePathDefaultsToNull()
    {
        var window = new WindowCandidate("w0", "notepad", "Untitled");
        Assert.Null(window.ExecutablePath);
    }

    // ── Duplicate shortcut / catalog dedup ────────────────────────────────────

    [Fact]
    public void AppEntry_SameDisplayNameEntries_DifferentAumids_AreDistinct()
    {
        // Two entries with different AUMIDs must be separate logical apps even if process matches.
        var chrome = new AppEntry("google-chrome", "Google Chrome", "chrome",
            AppLaunchKind.Win32, @"C:\chrome.exe", AppUserModelId: ChromeAumid);
        var ytMusic = new AppEntry("youtube-music", "YouTube Music", "chrome",
            AppLaunchKind.Win32, @"C:\chrome_proxy.exe",
            LaunchArguments: "--app-id=xyz",
            AppUserModelId: YtMusicAumid);

        Assert.NotEqual(chrome.AppUserModelId, ytMusic.AppUserModelId);
        Assert.NotEqual(chrome.Id, ytMusic.Id);
    }

    // ── CandidateBuilder propagation ──────────────────────────────────────────

    [Fact]
    public void CandidateBuilder_GetInstalledApps_PropagatesAumid()
    {
        var catalog = new StubCatalog(
            new AppEntry("chrome", "Google Chrome", "chrome", AppLaunchKind.Win32, @"C:\chrome.exe",
                AppUserModelId: ChromeAumid),
            new AppEntry("youtube-music", "YouTube Music", "chrome", AppLaunchKind.Win32, @"C:\chrome_proxy.exe",
                LaunchArguments: "--app-id=xyz", AppUserModelId: YtMusicAumid));

        var apps = CandidateBuilder.GetInstalledApps(catalog);

        Assert.Equal(ChromeAumid, apps.First(a => a.Id == "chrome").AppUserModelId);
        Assert.Equal(YtMusicAumid, apps.First(a => a.Id == "youtube-music").AppUserModelId);
    }

    [Fact]
    public void CandidateBuilder_GetInstalledApps_NullAumidPreserved()
    {
        var catalog = new StubCatalog(
            new AppEntry("notepad", "Notepad", "notepad", AppLaunchKind.Win32, "notepad.exe"));

        var apps = CandidateBuilder.GetInstalledApps(catalog);
        Assert.Null(apps.Single().AppUserModelId);
    }

    // ── Builder / stubs ────────────────────────────────────────────────────────

    private static PlanExecutor Build(
        StubLauncher? launcher = null,
        StubWindowService? windows = null)
        => new(
            launcher ?? new StubLauncher(),
            windows ?? new StubWindowService(),
            new StubMediaService(),
            new StubVolumeService(),
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
        public string? LastFocusedId { get; private set; }

        public ExecutionResult Focus(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot)
        {
            LastFocusedId = windowCandidateId;
            if (string.IsNullOrEmpty(windowCandidateId))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "No ID");
            if (!snapshot.Any(w => w.Id == windowCandidateId))
                return ExecutionResult.Fail(ExecutionStatus.WindowNotFound, "Not in snapshot");
            return FocusResult;
        }

        public ExecutionResult Close(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot) => ExecutionResult.Ok("closed");
        public ExecutionResult Maximize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot) => ExecutionResult.Ok("maximized");
        public ExecutionResult Minimize(string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot) => ExecutionResult.Ok("minimized");
        public ExecutionResult Snap(SnapDirection dir, string? windowCandidateId, IReadOnlyList<WindowCandidate> snapshot) => ExecutionResult.Ok("snapped");
    }

    private sealed class StubMediaService : IMediaService
    {
        public ExecutionResult Send(MediaOperation op) => ExecutionResult.Ok(op.ToString());
    }

    private sealed class StubVolumeService : IVolumeService
    {
        public ExecutionResult SetVolume(int percent) => ExecutionResult.Ok("ok");
        public ExecutionResult AdjustVolume(VolumeDirection dir, int? amount = null) => ExecutionResult.Ok("ok");
    }

    private sealed class StubCatalog : IAppCatalog
    {
        private readonly IReadOnlyList<AppEntry> _entries;
        public StubCatalog(params AppEntry[] entries) => _entries = entries;
        public IReadOnlyList<AppEntry> GetAll() => _entries;
        public AppEntry? FindById(string id) => _entries.FirstOrDefault(e => e.Id == id);
    }
}
