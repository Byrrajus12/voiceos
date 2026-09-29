using System.Text.Json;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Activation;

public sealed class FrontDoorContextBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");
    private static FrontDoorContext Build(string text, WindowCandidate? fg = null,
        IReadOnlyList<AppCandidate>? apps = null, bool connected = true,
        IReadOnlyList<BrowserTabInfo>? tabs = null, RecentTaskFrame? recent = null)
        => FrontDoorContextBuilder.Build(text, new(fg, fg is null ? [] : [fg], connected,
            tabs ?? [], recent, apps ?? []), recent, Now);

    [Fact]
    public void ForegroundNativeApp_IsReported_WithNamedMatches()
    {
        var context = Build("Close Character Map", new("w0", "charmap", "Character Map", true, 123),
            [new("charmap", "Character Map", "charmap")]);
        Assert.Equal(ForegroundKind.NativeApp, context.Foreground!.Kind);
        Assert.Contains(context.NamedMatches, m => m.Name == "Character Map" && m.Kind == NamedMatchKind.OpenWindow);
        Assert.Contains(context.NamedMatches, m => m.Kind == NamedMatchKind.InstalledApp);
        Assert.DoesNotContain("123", JsonSerializer.Serialize(context.ToJevState()));
    }

    [Fact]
    public void ActiveBrowserPage_ReportsSiteAndTitle_NoPathOrQuery()
    {
        var context = Build("open the third result", new("w", "chrome", "Raw browser title", true), tabs:
            [new(99, 1, true, "https://example.org/private?q=secret", "Search results", BrowserTabProvenance.User)]);
        var json = JsonSerializer.Serialize(context.ToJevState());
        Assert.Equal(ForegroundKind.Browser, context.Foreground!.Kind);
        Assert.Equal("example.org", context.Browser.ActiveTab!.Site);
        Assert.Contains("Search results", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("Raw browser title", json);
        Assert.DoesNotContain("99", json);
    }

    [Fact]
    public void NamedInstalledAppVsWebService_BothKindsListed()
    {
        var context = Build("Open Spotify", apps: [new("spotify", "Spotify", "Spotify")]);
        Assert.Contains(context.NamedMatches, m => m.Kind == NamedMatchKind.InstalledApp);
        Assert.Contains(context.NamedMatches, m => m.Kind == NamedMatchKind.WebService);
    }

    [Fact]
    public void NoCompanion_ReportsDisconnected_NoTabs()
    {
        var context = Build("YouTube", connected: false, tabs:
            [new(1, 1, true, "https://youtube.com/", "YouTube", BrowserTabProvenance.User)]);
        Assert.Null(context.Browser.ActiveTab);
        Assert.Empty(context.Browser.MatchingTabs);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(context.ToJevState()));
        Assert.Equal(0, json.RootElement.GetProperty("browser").GetProperty("open_tabs").GetInt32());
    }

    [Fact]
    public void RecentOwnedTab_StillActiveFlag()
    {
        var frame = new RecentTaskFrame(7, "s", "https://example.org/", "Find keyboards",
            InteractionCompletionState.Complete, Now.AddSeconds(-41));
        var context = Build("open it", tabs:
            [new(7, 1, true, frame.Url, "Keyboards", BrowserTabProvenance.VoiceOs, "s")], recent: frame);
        Assert.True(context.RecentTask!.StillActiveTab);
        Assert.True(context.RecentTask.Completed);
        Assert.Equal(41, context.RecentTask.SecondsAgo);
    }

    [Fact]
    public void NoMatchUtterance_EmptyMatches()
        => Assert.Empty(Build("Turn it up a little", apps: [new("spotify", "Spotify")]).NamedMatches);

    [Fact]
    public void BrowserWindowTitles_NeverUsedForMatching()
        => Assert.Empty(Build("Close Character Map", new("w", "chrome", "Character Map - Chrome")).NamedMatches);

    [Fact]
    public void ContextBound_WorstCase()
    {
        var apps = Enumerable.Range(0, 400).Select(i => new AppCandidate($"a{i}", "Character Map " + new string('x', 100))).ToArray();
        var tabs = Enumerable.Range(0, 60).Select(i => new BrowserTabInfo(i, 1, i == 0,
            "https://example.org/private", "Character Map\n\t" + new string('x', 200), BrowserTabProvenance.User)).ToArray();
        var context = Build("Character Map " + new string('x', 100), new("w", "charmap", new string('x', 200)), apps, tabs: tabs);
        var json = JsonSerializer.Serialize(context.ToJevState());
        Assert.True(json.Length <= FrontDoorContext.MaxSerializedChars, json);
        Assert.True(context.NamedMatches.Count <= 5);
        Assert.True(context.Browser.MatchingTabs.Count <= 3);
        Assert.DoesNotContain("\\n", json);
        Assert.DoesNotContain("\\t", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("elements", json);
        Assert.DoesNotContain("visible_text", json);
        Assert.DoesNotContain("apps", json);
    }

    [Theory]
    [InlineData('漢')]
    [InlineData('"')]
    public void ContextBound_IncludesJsonEscaping(char character)
    {
        var text = new string(character, 200);
        var context = new FrontDoorContext(new(text, text, ForegroundKind.NativeApp, 1),
            Enumerable.Range(0, 5).Select(i => new NamedMatch(text, NamedMatchKind.OpenWindow, false, 3)).ToArray(),
            new(true, 60, new(1, text, text, true, true),
                Enumerable.Range(0, 3).Select(i => new TabFact(i, text, text, false, true)).ToArray()),
            new(text, text, true, 41, true));
        Assert.True(JsonSerializer.Serialize(context.ToJevState()).Length <= 1500);
    }

    [Theory]
    [InlineData("Close Character Map", "Character Map", 3)]
    [InlineData("map character", "Character Map", 2)]
    [InlineData("Character", "Character Map", 1)]
    [InlineData("turn it up", "Character Map", 0)]
    public void NamedMatchScores_AreDeterministic(string utterance, string candidate, int score)
        => Assert.Equal(score, FrontDoorContextBuilder.MatchNames(utterance, candidate));
}
