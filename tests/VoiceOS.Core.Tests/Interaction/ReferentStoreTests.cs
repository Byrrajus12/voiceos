using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Interaction;

public sealed class ReferentStoreTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");

    internal static Referent Page(int tab, string url, string label = "P", string session = "s", DateTimeOffset? seen = null, bool owned = true)
        => new(ReferentKind.Page, label, ReferentProvenance.NavigationResult, seen ?? Now, tab, session, url, OwnedByVoiceOs: owned);
    internal static Referent Item(string href, string label = "I", string? source = null, DateTimeOffset? seen = null)
        => new(ReferentKind.Item, label, ReferentProvenance.ActivatedTarget, seen ?? Now, 1, "s", Href: href, SourceUrl: source);
    internal static Referent Window(nint hwnd, string process = "notepad", string title = "Untitled")
        => new(ReferentKind.AppWindow, title, ReferentProvenance.DirectStepResult, Now, Hwnd: hwnd, ProcessName: process);
    internal static BrowserTabInfo Tab(int id, string url, string? title = "T", string? session = "s", bool active = false,
        BrowserTabProvenance provenance = BrowserTabProvenance.VoiceOs)
        => new(id, 1, active, url, title, provenance, session);
    internal static ReferentInventory Inv(IEnumerable<BrowserTabInfo>? tabs = null, IEnumerable<WindowCandidate>? windows = null, bool known = true)
        => new(known, (tabs ?? []).ToArray(), (windows ?? []).ToArray());
    internal static WindowCandidate Win(nint hwnd, string process = "notepad") => new($"w{hwnd}", process, "t", Hwnd: hwnd);

    [Fact]
    public void ObserveTwice_DedupsAndRefreshesRecency()
    {
        var store = new ReferentStore();
        store.Observe(Item("https://a.test/x"));
        store.Observe(Item("https://b.test/y"));
        store.Observe(Item("https://a.test/x", "renamed"));
        var all = store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("renamed", all[0].Label);
    }

    [Fact]
    public void PageForSameTab_IsReplacedWhenTabNavigates()
    {
        var store = new ReferentStore();
        store.Observe(Page(1, "https://a.test/1"));
        store.Observe(Page(1, "https://a.test/2"));
        var only = Assert.Single(store.All());
        Assert.Equal("https://a.test/2", only.Url);
    }

    [Fact]
    public void Store_IsBounded_DroppingOldest()
    {
        var store = new ReferentStore(maxEntries: 3);
        for (var i = 0; i < 6; i++) store.Observe(Item($"https://a.test/{i}"));
        Assert.Equal(3, store.Count);
        Assert.Equal(["https://a.test/5", "https://a.test/4", "https://a.test/3"], store.All().Select(r => r.Href));
    }

    [Fact]
    public void Validate_ExpiresByTtl()
    {
        var store = new ReferentStore(ttl: TimeSpan.FromMinutes(10));
        store.Observe(Item("https://a.test/old", seen: Now.AddMinutes(-11)));
        store.Observe(Item("https://a.test/new", seen: Now.AddMinutes(-1)));
        var valid = store.Validate(Inv(), Now);
        Assert.Equal("https://a.test/new", Assert.Single(valid).Href);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Validate_RecencyOrderIsMostRecentFirst()
    {
        var store = new ReferentStore();
        store.Observe(Item("https://a.test/1"));
        store.Observe(Item("https://a.test/2"));
        Assert.Equal(["https://a.test/2", "https://a.test/1"], store.Validate(Inv(), Now).Select(r => r.Href));
    }

    [Fact]
    public void Page_IsInvalidWhenTabClosed_SessionChanged_OrUrlMoved()
    {
        var store = new ReferentStore();
        store.Observe(Page(1, "https://a.test/1"));
        store.Observe(Page(2, "https://a.test/2"));
        store.Observe(Page(3, "https://a.test/3"));
        var valid = store.Validate(Inv([
            Tab(2, "https://a.test/2", session: "other"), // session changed
            Tab(3, "https://a.test/moved")]), Now);      // url moved; tab 1 absent (closed)
        Assert.Empty(valid);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Page_ValidWhileTabShowsIt_AndTitleComesFromInventory()
    {
        var store = new ReferentStore();
        store.Observe(Page(1, "https://a.test/1", "stale title"));
        var page = Assert.Single(store.Validate(Inv([Tab(1, "https://a.test/1", "Fresh")]), Now));
        Assert.Equal("Fresh", page.Label);
    }

    [Fact]
    public void BrowserInventoryUnavailable_KeepsButDoesNotExposeBrowserReferents()
    {
        var store = new ReferentStore();
        store.Observe(Page(1, "https://a.test/1"));
        Assert.Empty(store.Validate(Inv(known: false), Now));
        Assert.Equal(1, store.Count);
        Assert.Single(store.Validate(Inv([Tab(1, "https://a.test/1")]), Now));
    }

    [Fact]
    public void Item_SurvivesItsTabClosing_ButNotABadHref()
    {
        var store = new ReferentStore();
        store.Observe(Item("https://a.test/x"));
        store.Observe(Item("javascript:alert(1)"));
        Assert.Equal("https://a.test/x", Assert.Single(store.Validate(Inv(), Now)).Href);
    }

    [Fact]
    public void Window_InvalidatedWhenHwndGoneOrProcessDiffers()
    {
        var store = new ReferentStore();
        store.Observe(Window(10));
        store.Observe(Window(11));
        store.Observe(Window(12));
        var valid = store.Validate(Inv(windows: [Win(10), Win(11, "calc")]), Now);
        Assert.Equal(10, (int)Assert.Single(valid).Hwnd);
    }

    [Fact]
    public void InvalidateWindow_RemovesIt()
    {
        var store = new ReferentStore();
        store.Observe(Window(10));
        store.InvalidateWindow(10);
        Assert.Empty(store.Validate(Inv(windows: [Win(10)]), Now));
    }

    [Fact]
    public void UserTab_WithoutSession_IsMatchedOnlyWhenNotOwned()
    {
        var store = new ReferentStore();
        store.Observe(Page(1, "https://a.test/1", owned: false, session: "act1"));
        var valid = store.Validate(Inv([Tab(1, "https://a.test/1", session: null, provenance: BrowserTabProvenance.User)]), Now);
        Assert.False(Assert.Single(valid).OwnedByVoiceOs);
    }

    [Fact]
    public void StoreExposesNoModelWriteSurface()
        => Assert.DoesNotContain(typeof(ReferentStore).GetMethods().Select(m => m.Name),
            n => n.Contains("Model") || n.Contains("Infer") || n.Contains("Summar"));
}
