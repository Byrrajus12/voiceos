using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Execution;

/// <summary>
/// Tests WindowAwareLauncher argument selection and HWND snapshot logic.
/// The polling loop itself is validated physically; these tests cover the argument
/// routing, new-instance metadata, and identity invariants without real Windows calls.
/// </summary>
public class WindowAwareLauncherTests
{
    private const string ChromeAumid = "Chrome";
    private const string YtMusicAumid = "Chrome._crx_cinhimbnkkaeohfgghhklpknlkffjgod";

    private static WindowCandidate MakeWindow(string id, string proc, nint hwnd,
        string? aumid = null)
        => new(id, proc, $"{proc} - Window", Hwnd: hwnd, AppUserModelId: aumid);

    // ── 1. Chrome NewInstance resolves last-used profile dynamically ──────────

    [Fact]
    public async Task LaunchNewAsync_Chrome_ResolvesDynamicProfileArgs()
    {
        var localState = Path.GetTempFileName();
        try
        {
            File.WriteAllText(localState, """{"profile":{"last_used":"Profile 1"}}""");

            var entry = ChromeEntry();
            var capturedPsi = new List<ProcessStartInfo>();
            int call = 0;
            var newWindow = MakeWindow("w1", "chrome", hwnd: 200, aumid: ChromeAumid);

            var provider = new ChromeNewInstanceArgsProvider(localState);
            var launcher = Build(entry,
                getWindows: () => call++ == 0 ? [] : [newWindow],
                launch: psi => capturedPsi.Add(psi),
                argsProvider: provider);

            var result = await launcher.LaunchNewAsync(new AppTarget("google-chrome"));

            Assert.True(result.Succeeded);
            Assert.Single(capturedPsi);
            Assert.Contains("--profile-directory=\"Profile 1\"", capturedPsi[0].Arguments);
            Assert.Contains("--new-window", capturedPsi[0].Arguments);
        }
        finally
        {
            File.Delete(localState);
        }
    }

    // ── 2. FocusOrLaunch with existing Chrome window: focuses, never launches ──

    [Fact]
    public async Task FocusOrLaunchAsync_ExistingChromeWindow_FocusesWithoutLaunch()
    {
        var entry = ChromeEntry();
        var existing = MakeWindow("w0", "chrome", hwnd: 100, aumid: ChromeAumid);
        bool launchCalled = false;

        var launcher = Build(entry,
            getWindows: () => [existing],
            launch: _ => launchCalled = true,
            focusResult: ExecutionResult.Ok("focused"));

        var result = await launcher.FocusOrLaunchAsync(
            new AppTarget("google-chrome"), [existing]);

        Assert.True(result.Succeeded);
        Assert.False(launchCalled);
    }

    // ── 3. Apps without NewInstanceArguments use LaunchArguments for both paths

    [Fact]
    public async Task LaunchNewAsync_AppWithoutNewInstanceArgs_UsesLaunchArguments()
    {
        // Windows Terminal has no NewInstanceArguments in the catalog
        var entry = new AppEntry("windows-terminal", "Windows Terminal", "WindowsTerminal",
            AppLaunchKind.Win32, @"C:\wt.exe",
            LaunchArguments: null,
            NewInstanceArguments: null);

        var capturedPsi = new List<ProcessStartInfo>();
        int call = 0;
        var newWindow = MakeWindow("w1", "WindowsTerminal", hwnd: 300);

        var launcher = Build(entry,
            getWindows: () => call++ == 0 ? [] : [newWindow],
            launch: psi => capturedPsi.Add(psi));

        await launcher.LaunchNewAsync(new AppTarget("windows-terminal"));

        Assert.Single(capturedPsi);
        Assert.Equal("", capturedPsi[0].Arguments);
    }

    [Fact]
    public async Task LaunchNewAsync_AppWithLaunchArgsAndNoNewInstanceArgs_UsesLaunchArgs()
    {
        // App with launch args but no NewInstanceArguments: LaunchArguments used even for new instance
        var entry = new AppEntry("youtube-music", "YouTube Music", "chrome",
            AppLaunchKind.Win32, @"C:\chrome_proxy.exe",
            LaunchArguments: "--profile-directory=Default --app-id=cinhimbnkk",
            AppUserModelId: YtMusicAumid,
            NewInstanceArguments: null);

        var capturedPsi = new List<ProcessStartInfo>();
        int call = 0;
        var newWindow = MakeWindow("w1", "chrome", hwnd: 400, aumid: YtMusicAumid);

        var launcher = Build(entry,
            getWindows: () => call++ == 0 ? [] : [newWindow],
            launch: psi => capturedPsi.Add(psi));

        await launcher.LaunchNewAsync(new AppTarget("youtube-music"));

        Assert.Single(capturedPsi);
        Assert.Equal("--profile-directory=Default --app-id=cinhimbnkk", capturedPsi[0].Arguments);
    }

    // ── 4. NewInstance snapshots existing matching HWNDs before launch ─────────

    [Fact]
    public async Task LaunchNewAsync_SnapshotsExistingHwndsBeforeLaunch()
    {
        var entry = ChromeEntry();
        var existingChrome = MakeWindow("w0", "chrome", hwnd: 100, aumid: ChromeAumid);
        var newChrome = MakeWindow("w1", "chrome", hwnd: 200, aumid: ChromeAumid);

        int call = 0;
        // Pre-launch snapshot has existing Chrome; post-launch has both
        var launcher = Build(entry,
            getWindows: () => call++ == 0
                ? [existingChrome]
                : [existingChrome, newChrome],
            launch: _ => { });

        var result = await launcher.LaunchNewAsync(new AppTarget("google-chrome"));

        Assert.True(result.Succeeded);
        // Must return the NEW window (hwnd 200), not the pre-existing one
        Assert.Equal(200, result.ResultWindow?.Hwnd);
    }

    // ── 5. Only a newly appearing matching HWND can satisfy NewInstance ─────────

    [Fact]
    public async Task LaunchNewAsync_PreExistingHwnd_CannotSatisfyNewInstance()
    {
        var entry = ChromeEntry();
        var existingChrome = MakeWindow("w0", "chrome", hwnd: 100, aumid: ChromeAumid);

        // Windows never change after launch — existing Chrome is pre-launch, cannot satisfy
        var launcher = Build(entry,
            getWindows: () => [existingChrome],
            launch: _ => { });

        // Short timeout so test doesn't wait 8s
        launcher.LaunchTimeout = TimeSpan.FromMilliseconds(300);
        launcher.PollInterval = TimeSpan.FromMilliseconds(50);

        var result = await launcher.LaunchNewAsync(new AppTarget("google-chrome"));

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionStatus.WindowNotFound, result.Status);
    }

    // ── 6. YouTube Music PWA cannot satisfy normal Chrome NewInstance ───────────

    [Fact]
    public async Task LaunchNewAsync_Chrome_YtMusicWindowDoesNotSatisfy()
    {
        // Chrome catalog entry has AUMID "Chrome"
        var entry = ChromeEntry();

        // Pre-existing YT Music (different AUMID = hard reject for Chrome)
        var ytMusic = MakeWindow("w0", "chrome", hwnd: 100, aumid: YtMusicAumid);

        // After launch, a second YT Music window appears — still wrong AUMID
        var newYtMusic = MakeWindow("w1", "chrome", hwnd: 200, aumid: YtMusicAumid);

        int call = 0;
        var launcher = Build(entry,
            getWindows: () => call++ == 0 ? [ytMusic] : [ytMusic, newYtMusic],
            launch: _ => { });

        launcher.LaunchTimeout = TimeSpan.FromMilliseconds(300);
        launcher.PollInterval = TimeSpan.FromMilliseconds(50);

        var result = await launcher.LaunchNewAsync(new AppTarget("google-chrome"));

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionStatus.WindowNotFound, result.Status);
    }

    // ── 7. Existing StepResultTarget collision regression ───────────────────────
    // (Covered by ProgramExecutorTests — confirmed passing; referenced for traceability.)

    // ── FocusOrLaunch: no Chrome window open → launches with normal args ────────
    // Provider is injected but FocusOrLaunch must not route through it.

    [Fact]
    public async Task FocusOrLaunchAsync_NoExistingWindow_DoesNotUseProfileAwareArgs()
    {
        var localState = Path.GetTempFileName();
        try
        {
            File.WriteAllText(localState, """{"profile":{"last_used":"Profile 1"}}""");

            var entry = ChromeEntry();
            var capturedPsi = new List<ProcessStartInfo>();
            int call = 0;
            var newWindow = MakeWindow("w0", "chrome", hwnd: 100, aumid: ChromeAumid);

            var provider = new ChromeNewInstanceArgsProvider(localState);
            var launcher = Build(entry,
                getWindows: () => call++ == 0 ? [] : [newWindow],
                launch: psi => capturedPsi.Add(psi),
                argsProvider: provider);

            var result = await launcher.FocusOrLaunchAsync(
                new AppTarget("google-chrome"), []);

            Assert.True(result.Succeeded);
            Assert.Single(capturedPsi);
            Assert.DoesNotContain("--profile-directory", capturedPsi[0].Arguments);
            Assert.DoesNotContain("--new-window", capturedPsi[0].Arguments);
        }
        finally
        {
            File.Delete(localState);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static AppEntry ChromeEntry() => new(
        "google-chrome", "Google Chrome", "chrome",
        AppLaunchKind.Win32, @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        LaunchArguments: null,
        AppUserModelId: ChromeAumid,
        NewInstanceArguments: null);

    private static WindowAwareLauncher Build(
        AppEntry entry,
        Func<IReadOnlyList<WindowCandidate>>? getWindows = null,
        Action<ProcessStartInfo>? launch = null,
        ExecutionResult? focusResult = null,
        INewInstanceArgsProvider? argsProvider = null)
    {
        var catalog = new SingleEntryStubCatalog(entry);
        var windows = new StubWindowService(focusResult ?? ExecutionResult.Ok("focused"));
        return new WindowAwareLauncher(
            catalog,
            windows,
            NullLogger<WindowAwareLauncher>.Instance,
            getWindows: getWindows ?? (() => []),
            launch: launch ?? (_ => { }),
            newInstanceArgsProvider: argsProvider);
    }

    private sealed class SingleEntryStubCatalog : IAppCatalog
    {
        private readonly AppEntry _entry;
        public SingleEntryStubCatalog(AppEntry entry) => _entry = entry;
        public IReadOnlyList<AppEntry> GetAll() => [_entry];
        public AppEntry? FindById(string id) =>
            string.Equals(id, _entry.Id, StringComparison.OrdinalIgnoreCase) ? _entry : null;
    }

    private sealed class StubWindowService : IWindowService
    {
        private readonly ExecutionResult _focusResult;
        public StubWindowService(ExecutionResult focusResult) => _focusResult = focusResult;
        public nint GetForegroundWindowHwnd() => 0;
        public ExecutionResult Focus(string? id, IReadOnlyList<WindowCandidate> snap) => _focusResult;
        public ExecutionResult Close(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Maximize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Minimize(string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
        public ExecutionResult Snap(SnapDirection dir, string? id, IReadOnlyList<WindowCandidate> snap) => ExecutionResult.Ok("ok");
    }
}
