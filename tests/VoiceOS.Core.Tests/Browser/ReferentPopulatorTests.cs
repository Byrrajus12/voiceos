using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ReferentPopulatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    private const string Session = "sess";

    private static TypedRef Link(string label, string href, int tab = 1) => new(tab, Session, 1, "e1", "fp-" + label, label, "link", href);
    private static Effect Activated(TypedRef subject, string action) => new(EffectKind.Activated, EffectSource.CompanionResponse,
        EffectStrength.Observed, subject) { ActionId = action };
    private static Effect Navigated(string from, string to, string action, TypedRef? subject = null) => new(EffectKind.Navigated,
        EffectSource.SnapshotDelta, EffectStrength.Derived, subject,
        new Dictionary<string, string> { ["from"] = from, ["to"] = to }) { ActionId = action };
    private static Effect Acquired(int tab, string mode, int? from = null) => new(EffectKind.SurfaceAcquired,
        EffectSource.CompanionResponse, EffectStrength.Observed, new(tab, Session, 1, null, "pg", "Page", "page", "https://x.test/"),
        from is null ? new Dictionary<string, string> { ["mode"] = mode }
            : new Dictionary<string, string> { ["mode"] = mode, ["fromTabId"] = from.ToString()! });

    private static IReadOnlyList<Referent> Run(IReadOnlyList<Effect>? effects, InteractionCompletionState completion,
        string url = "https://imdb.test/dune21", string title = "Dune (2021)", int tab = 1)
        => ReferentPopulator.FromBrowserRun(effects, completion, tab, Session, url, title, Now);

    [Fact]
    public void ActivatedThenNavigated_StoresItemAndPage()
    {
        var link = Link("Dune (2021)", "https://imdb.test/dune21");
        var result = Run([Activated(link, "a1"), Navigated("https://imdb.test/find", "https://imdb.test/dune21", "a1", link)],
            InteractionCompletionState.Complete);
        var item = Assert.Single(result, r => r.Kind == ReferentKind.Item);
        Assert.Equal("Dune (2021)", item.Label);
        Assert.Equal("https://imdb.test/dune21", item.Href);
        Assert.Equal("https://imdb.test/find", item.SourceUrl);
        Assert.Equal(ReferentProvenance.ActivatedTarget, item.Provenance);
        var page = Assert.Single(result, r => r.Kind == ReferentKind.Page);
        Assert.Equal(ReferentProvenance.NavigationResult, page.Provenance);
        Assert.Equal("Dune (2021)", page.Label);
    }

    [Fact]
    public void SurfaceAcquired_StoresOwnedTabAndPage()
    {
        var result = Run([Acquired(1, "opened")], InteractionCompletionState.Complete, "https://x.test/", "X");
        Assert.Contains(result, r => r.Kind == ReferentKind.BrowserTab && r.OwnedByVoiceOs);
        Assert.Contains(result, r => r.Kind == ReferentKind.Page && r.Provenance == ReferentProvenance.SurfaceAcquired);
        Assert.DoesNotContain(result, r => r.Kind == ReferentKind.Item);
    }

    [Fact]
    public void NavigationWithoutActivation_UpdatesPageOnly()
    {
        var back = new Effect(EffectKind.HistoryMoved, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["from"] = "a", ["to"] = "b" });
        var result = Run([back], InteractionCompletionState.Complete, "https://b.test/", "B");
        var page = Assert.Single(result);
        Assert.Equal(ReferentKind.Page, page.Kind);
        Assert.Equal(ReferentProvenance.NavigationResult, page.Provenance);
    }

    [Fact]
    public void MeansActivation_IsNotTheFinalItem()
    {
        var first = Link("Search", "https://imdb.test/find?q=dune");
        var final = Link("Dune (2021)", "https://imdb.test/dune21");
        var result = Run([
            Activated(first, "a1"), Navigated("https://imdb.test/", "https://imdb.test/find?q=dune", "a1", first),
            Activated(final, "a2"), Navigated("https://imdb.test/find?q=dune", "https://imdb.test/dune21", "a2", final)],
            InteractionCompletionState.Complete);
        Assert.Equal("Dune (2021)", Assert.Single(result, r => r.Kind == ReferentKind.Item).Label);
    }

    [Fact]
    public void LastActivationWithoutObservedLanding_DoesNotFallBackToEarlierItem()
    {
        var first = Link("Search", "https://imdb.test/find");
        var final = Link("Dune (2021)", "https://imdb.test/dune21");
        var noEffect = new Effect(EffectKind.NoEffect, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["reason"] = "NAVIGATION_NOT_OBSERVED" });
        var result = Run([
            Activated(first, "a1"), Navigated("https://imdb.test/", "https://imdb.test/find", "a1", first),
            Activated(final, "a2"), noEffect], InteractionCompletionState.Complete, "https://imdb.test/find", "Find");
        Assert.DoesNotContain(result, r => r.Kind == ReferentKind.Item);
    }

    [Fact]
    public void ActivationFollowedByAnotherNavigation_IsNotTheItem()
    {
        var link = Link("Dune (2021)", "https://imdb.test/dune21");
        var back = new Effect(EffectKind.HistoryMoved, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["from"] = "https://imdb.test/dune21", ["to"] = "https://imdb.test/find" });
        var result = Run([Activated(link, "a1"), Navigated("https://imdb.test/find", "https://imdb.test/dune21", "a1", link), back],
            InteractionCompletionState.Complete, "https://imdb.test/find", "Find");
        Assert.DoesNotContain(result, r => r.Kind == ReferentKind.Item);
    }

    [Fact]
    public void AdoptedChildTab_ConfirmsTheActivationAsItem()
    {
        var link = Link("Docs", "https://docs.test/x");
        var result = Run([Activated(link, "a1"), Acquired(2, "adopted", from: 1)], InteractionCompletionState.Complete,
            "https://docs.test/x", "Docs", tab: 2);
        Assert.Equal("https://docs.test/x", Assert.Single(result, r => r.Kind == ReferentKind.Item).Href);
        Assert.Contains(result, r => r.Kind == ReferentKind.Page && r.TabId == 2);
    }

    [Theory]
    [InlineData(InteractionCompletionState.Incomplete)]
    public void FailedRun_StoresNothing(InteractionCompletionState completion)
    {
        var link = Link("Dune (2021)", "https://imdb.test/dune21");
        Assert.Empty(Run([Activated(link, "a1"), Navigated("f", "https://imdb.test/dune21", "a1", link)], completion));
    }

    [Fact]
    public void UncertainRun_StoresPageButNeverItem()
    {
        var link = Link("Dune (2021)", "https://imdb.test/dune21");
        var result = Run([Activated(link, "a1"), Navigated("f", "https://imdb.test/dune21", "a1", link)],
            InteractionCompletionState.Uncertain);
        Assert.Single(result);
        Assert.Equal(ReferentKind.Page, result[0].Kind);
    }

    [Fact]
    public void NoTabOrSession_StoresNothing()
        => Assert.Empty(ReferentPopulator.FromBrowserRun([], InteractionCompletionState.Complete, null, null, "https://x.test/", "X", Now));

    [Fact]
    public void JavascriptHref_IsNeverStoredAsItemAddress()
    {
        var link = Link("Menu", "javascript:void(0)");
        var result = Run([Activated(link, "a1"), Navigated("f", "https://imdb.test/dune21", "a1", link)], InteractionCompletionState.Complete);
        Assert.Equal("https://imdb.test/dune21", Assert.Single(result, r => r.Kind == ReferentKind.Item).Href);
    }
}
