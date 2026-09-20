using VoiceOS.Core.Candidates;
using Xunit;

namespace VoiceOS.Core.Tests.Candidates;

public class CandidateBuilderTests
{
    [Fact]
    public void GetInstalledApps_ReturnsCatalog()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        Assert.NotEmpty(apps);
    }

    [Fact]
    public void GetInstalledApps_ContainsExpectedEntries()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        Assert.Contains(apps, a => a.Id == "chrome" && a.DisplayName == "Google Chrome");
        Assert.Contains(apps, a => a.Id == "vscode" && a.DisplayName == "Visual Studio Code");
        Assert.Contains(apps, a => a.Id == "spotify" && a.DisplayName == "Spotify");
        Assert.Contains(apps, a => a.Id == "terminal" && a.DisplayName == "Windows Terminal");
    }

    [Fact]
    public void GetInstalledApps_AllHaveNonEmptyIds()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        Assert.All(apps, a => Assert.NotEmpty(a.Id));
    }

    [Fact]
    public void GetInstalledApps_AllHaveNonEmptyDisplayNames()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        Assert.All(apps, a => Assert.NotEmpty(a.DisplayName));
    }

    [Fact]
    public void GetInstalledApps_IdsAreUnique()
    {
        var apps = CandidateBuilder.GetInstalledApps();
        var ids = apps.Select(a => a.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count);
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
}
