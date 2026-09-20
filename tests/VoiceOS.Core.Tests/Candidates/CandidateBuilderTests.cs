using VoiceOS.Core.Apps;
using VoiceOS.Core.Candidates;
using Xunit;

namespace VoiceOS.Core.Tests.Candidates;

public class CandidateBuilderTests
{
    // Stub catalog for CandidateBuilder tests — avoids real Windows discovery in unit tests.
    private static IAppCatalog MakeCatalog(params AppEntry[] entries) => new StubCatalog(entries);

    [Fact]
    public void GetInstalledApps_FromCatalog_ReturnsMappedCandidates()
    {
        var catalog = MakeCatalog(
            new AppEntry("chrome", "Google Chrome", "chrome", AppLaunchKind.Win32, @"C:\chrome.exe"),
            new AppEntry("vscode", "Visual Studio Code", "Code", AppLaunchKind.Win32, @"C:\code.exe"));

        var apps = CandidateBuilder.GetInstalledApps(catalog);

        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.Id == "chrome" && a.DisplayName == "Google Chrome");
        Assert.Contains(apps, a => a.Id == "vscode" && a.DisplayName == "Visual Studio Code");
    }

    [Fact]
    public void GetInstalledApps_AllHaveNonEmptyIds()
    {
        var catalog = MakeCatalog(
            new AppEntry("a", "App A", null, AppLaunchKind.Win32, "a.exe"),
            new AppEntry("b", "App B", null, AppLaunchKind.Win32, "b.exe"));

        var apps = CandidateBuilder.GetInstalledApps(catalog);
        Assert.All(apps, a => Assert.NotEmpty(a.Id));
    }

    [Fact]
    public void GetInstalledApps_AllHaveNonEmptyDisplayNames()
    {
        var catalog = MakeCatalog(
            new AppEntry("x", "My App", null, AppLaunchKind.Win32, "x.exe"));

        var apps = CandidateBuilder.GetInstalledApps(catalog);
        Assert.All(apps, a => Assert.NotEmpty(a.DisplayName));
    }

    [Fact]
    public void GetInstalledApps_EmptyCatalog_ReturnsEmpty()
    {
        var apps = CandidateBuilder.GetInstalledApps(MakeCatalog());
        Assert.Empty(apps);
    }

    [Fact]
    public void WindowCandidate_ConstructionIsCorrect()
    {
        var w = new WindowCandidate("w0", "chrome", "Google - Chrome", IsForeground: true);
        Assert.Equal("w0", w.Id);
        Assert.Equal("chrome", w.ProcessName);
        Assert.Equal("Google - Chrome", w.Title);
        Assert.True(w.IsForeground);
    }

    [Fact]
    public void WindowCandidate_DefaultIsNotForeground()
    {
        var w = new WindowCandidate("w1", "notepad", "Untitled - Notepad");
        Assert.False(w.IsForeground);
    }

    [Fact]
    public void WindowCandidate_HwndDefaultsToZero()
    {
        var w = new WindowCandidate("w0", "proc", "Title");
        Assert.Equal((nint)0, w.Hwnd);
    }

    [Fact]
    public void WindowCandidate_HwndIsStored()
    {
        var w = new WindowCandidate("w0", "proc", "Title", Hwnd: 42);
        Assert.Equal((nint)42, w.Hwnd);
    }

    private sealed class StubCatalog : IAppCatalog
    {
        private readonly IReadOnlyList<AppEntry> _entries;
        public StubCatalog(AppEntry[] entries) => _entries = entries;
        public IReadOnlyList<AppEntry> GetAll() => _entries;
        public AppEntry? FindById(string id) => _entries.FirstOrDefault(e => e.Id == id);
    }
}
