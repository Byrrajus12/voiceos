using VoiceOS.Core.Browser;
using VoiceOS.Eval.Live;
using Xunit;

namespace VoiceOS.Eval.Tests;

public sealed class SetupCleanupTests
{
    private static TabInfo Tab(int id, string url, int window = 1, bool active = false)
        => new(id, window, active, url, url, BrowserTabProvenance.User, null, null);

    private static WindowInfo Window(string process, long hwnd) => new(process, process, false, hwnd);

    private static StateSnapshot State(TabInfo[]? tabs = null, WindowInfo[]? windows = null)
        => new(DateTimeOffset.UtcNow, null, windows ?? [], true, tabs ?? [], null, 1);

    private static readonly SetupStep[] NasaSetup =
    [
        new(SetupStepKind.StartProcess, "chrome.exe", "https://www.nasa.gov/"),
        new(SetupStepKind.Wait, Ms: 3000)
    ];

    [Fact]
    public void ClosesOnlyTheWebTabSetupOpened()
    {
        var before = State([Tab(1, "https://mail.google.com/"), Tab(2, "https://github.com/")]);
        var after = State([.. before.Tabs, Tab(3, "https://www.nasa.gov/", active: true)]);
        // During the turn the setup page navigated and the product opened its own task tab.
        var current = State([.. before.Tabs, Tab(3, "https://www.nasa.gov/news/"), Tab(4, "https://www.google.com/")]);

        var plan = SetupCleanup.Plan(NasaSetup, before, after, current);

        var tab = Assert.Single(plan.Tabs);
        Assert.Equal(3, tab.TabId);
        Assert.Equal("https://www.nasa.gov/news/", tab.Url);
        Assert.Empty(plan.Windows);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void UnknownInventoryNeverMakesExistingUserTabsLookSetupCreated(bool beforeKnown, bool afterKnown, bool currentKnown)
    {
        var before = State() with { BrowserConnected = beforeKnown };
        var after = State([Tab(1, "https://github.com/"), Tab(2, "https://www.nasa.gov/")])
            with { BrowserConnected = afterKnown };
        var current = after with { BrowserConnected = currentKnown };
        Assert.Empty(SetupCleanup.Plan(NasaSetup, before, after, current).Tabs);
    }

    [Fact]
    public void NeverClosesASetupTabThatIsAloneInItsWindow()
    {
        var before = State([Tab(1, "https://github.com/", window: 1)]);
        var after = State([.. before.Tabs, Tab(5, "https://www.nasa.gov/", window: 2)]);
        var plan = SetupCleanup.Plan(NasaSetup, before, after, after);
        Assert.Empty(plan.Tabs);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void AlreadyClosedOrNonWebTabsAreIgnored()
    {
        var before = State([Tab(1, "https://github.com/")]);
        var after = State([.. before.Tabs, Tab(3, "https://www.nasa.gov/"), Tab(4, "chrome://newtab/")]);
        var plan = SetupCleanup.Plan(NasaSetup, before, after, State([Tab(1, "https://github.com/"), Tab(4, "chrome://newtab/")]));
        Assert.Empty(plan.Tabs);
    }

    [Fact]
    public void ClosesNewWindowsOfSetupStartedAppsOnly()
    {
        SetupStep[] paintSetup = [new(SetupStepKind.StartProcess, "mspaint.exe")];
        var before = State(windows: [Window("mspaint", 10), Window("Code", 11)]);
        var current = State(windows: [Window("mspaint", 10), Window("Code", 11), Window("mspaint", 12),
            Window("Notepad", 13)]);
        var plan = SetupCleanup.Plan(paintSetup, before, current, current);
        Assert.Equal([12L], plan.Windows);
        Assert.Empty(plan.Tabs);
    }

    [Fact]
    public void ChromeSetupNeverClosesBrowserWindows()
    {
        var before = State(windows: [Window("chrome", 10)]);
        var current = State(windows: [Window("chrome", 10), Window("chrome", 20)]);
        Assert.Empty(SetupCleanup.Plan(NasaSetup, before, current, current).Windows);
    }

    [Fact]
    public void ForegroundFallsBackToRawWindowWhenNotEnumerated()
    {
        var enumerated = new[] { Window("Code", 11) };
        var raw = new WindowInfo("chrome", "Stack Overflow", false, 99);
        var foreground = SetupCleanup.Foreground(enumerated, () => raw);
        Assert.Equal("chrome", foreground?.ProcessName);
        Assert.Equal(99, foreground?.Hwnd);
        Assert.True(foreground?.IsForeground);
        Assert.False(foreground?.Enumerated);

        var listed = new WindowInfo("Notion", "Notion", true, 7);
        Assert.Same(listed, SetupCleanup.Foreground([listed], () => raw));
        Assert.Null(SetupCleanup.Foreground(enumerated, () => null));
    }
}
