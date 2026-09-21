using VoiceOS.Core.Apps;
using Xunit;

namespace VoiceOS.Core.Tests.Apps;

public class ChromeNewInstanceArgsProviderTests
{
    private const string ChromeAumid = "Chrome";
    private const string YtMusicAumid = "Chrome._crx_cinhimbnkkaeohfgghhklpknlkffjgod";

    private static AppEntry ChromeEntry(string? newInstanceArgs = null) => new(
        "google-chrome", "Google Chrome", "chrome",
        AppLaunchKind.Win32, @"C:\chrome.exe",
        AppUserModelId: ChromeAumid,
        NewInstanceArguments: newInstanceArgs);

    private static AppEntry NonChromeEntry(string? newInstanceArgs = null) => new(
        "windows-terminal", "Windows Terminal", "WindowsTerminal",
        AppLaunchKind.Win32, @"C:\wt.exe",
        NewInstanceArguments: newInstanceArgs);

    // ── 1. Reads last_used from Local State ───────────────────────────────────

    [Fact]
    public void GetNewInstanceArguments_Chrome_ReadsLastUsedProfile()
    {
        using var f = new TempLocalState("""{"profile":{"last_used":"Profile 1"}}""");
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        Assert.Contains("--profile-directory=\"Profile 1\"", args);
    }

    // ── 2. Result includes both --profile-directory and --new-window ──────────

    [Fact]
    public void GetNewInstanceArguments_Chrome_IncludesBothFlags()
    {
        using var f = new TempLocalState("""{"profile":{"last_used":"Default"}}""");
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        Assert.NotNull(args);
        Assert.Contains("--profile-directory=\"Default\"", args);
        Assert.Contains("--new-window", args);
    }

    // ── 3. Missing Local State falls back to "Default" ────────────────────────

    [Fact]
    public void GetNewInstanceArguments_Chrome_MissingLocalState_FallsBackToDefault()
    {
        var provider = new ChromeNewInstanceArgsProvider(@"C:\does-not-exist\Local State");

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        Assert.Contains("--profile-directory=\"Default\"", args);
        Assert.Contains("--new-window", args);
    }

    // ── 4. Corrupt/empty Local State falls back to "Default" ─────────────────

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"profile":{}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void GetNewInstanceArguments_Chrome_InvalidLocalState_FallsBackToDefault(string content)
    {
        using var f = new TempLocalState(content);
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        Assert.Contains("--profile-directory=\"Default\"", args);
    }

    // ── 5. Blank profile.last_used falls back to "Default" ───────────────────

    [Theory]
    [InlineData("""{"profile":{"last_used":""}}""")]
    [InlineData("""{"profile":{"last_used":"  "}}""")]
    public void GetNewInstanceArguments_Chrome_BlankLastUsed_FallsBackToDefault(string content)
    {
        using var f = new TempLocalState(content);
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        Assert.Contains("--profile-directory=\"Default\"", args);
    }

    // ── 6. Non-Chrome apps pass through entry.NewInstanceArguments ────────────

    [Fact]
    public void GetNewInstanceArguments_NonChrome_ReturnsEntryNewInstanceArguments()
    {
        var provider = new ChromeNewInstanceArgsProvider(@"C:\irrelevant");
        var entry = NonChromeEntry(newInstanceArgs: "--some-flag");

        var args = provider.GetNewInstanceArguments(entry);

        Assert.Equal("--some-flag", args);
    }

    [Fact]
    public void GetNewInstanceArguments_NonChrome_NullNewInstanceArgs_ReturnsNull()
    {
        var provider = new ChromeNewInstanceArgsProvider(@"C:\irrelevant");
        var entry = NonChromeEntry(newInstanceArgs: null);

        var args = provider.GetNewInstanceArguments(entry);

        Assert.Null(args);
    }

    // ── 7. Dynamic resolution: each call re-reads Local State ─────────────────
    // Verifies that profile.last_used changes between calls are picked up.

    [Fact]
    public void GetNewInstanceArguments_Chrome_ReadsFileOnEachCall()
    {
        using var f = new TempLocalState("""{"profile":{"last_used":"Default"}}""");
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var first = provider.GetNewInstanceArguments(ChromeEntry());
        Assert.Contains("--profile-directory=\"Default\"", first);

        File.WriteAllText(f.Path, """{"profile":{"last_used":"Profile 2"}}""");

        var second = provider.GetNewInstanceArguments(ChromeEntry());
        Assert.Contains("--profile-directory=\"Profile 2\"", second);
    }

    // ── 8. Profile directory name with spaces is quoted correctly ─────────────

    [Fact]
    public void GetNewInstanceArguments_Chrome_ProfileNameWithSpaces_IsQuoted()
    {
        using var f = new TempLocalState("""{"profile":{"last_used":"Profile 1"}}""");
        var provider = new ChromeNewInstanceArgsProvider(f.Path);

        var args = provider.GetNewInstanceArguments(ChromeEntry());

        // Must be quoted so the shell doesn't split "Profile" and "1" as separate tokens
        Assert.Contains("--profile-directory=\"Profile 1\"", args);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class TempLocalState : IDisposable
    {
        public string Path { get; }

        public TempLocalState(string content)
        {
            Path = System.IO.Path.GetTempFileName();
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
