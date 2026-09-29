using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Interaction;
using VoiceOS.Core.Tests.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class ReferentResolverTests
{
    private sealed class Chooser(Func<IReadOnlyList<ReferentCandidate>, ReferentChoice> choose) : IContextualScopeDecisionSource
    {
        public int Calls;
        public IReadOnlyList<ReferentCandidate> Offered = [];
        public ValueTask<ContextualSurface> SelectAsync(string utterance, CommandRouteDecision intent,
            ExecutionContextSnapshot context, IReadOnlyList<ContextualSurface> offered, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("surface picker must not be consulted");
        public ValueTask<ReferentChoice> SelectReferentAsync(string utterance, IReadOnlyList<ReferentCandidate> candidates,
            CancellationToken cancellationToken = default)
        { Calls++; Offered = candidates; return ValueTask.FromResult(choose(candidates)); }
    }

    private static Referent Seq(Referent r, long seq) => r with { Seq = seq };
    private static BrowserTabInfo Active(int id, string url) => ReferentStoreTests.Tab(id, url, active: true);

    private static IReadOnlyList<ReferentCandidate> Two(int activeTab = 1)
        => ReferentResolver.BuildCandidates([
            Seq(ReferentStoreTests.Page(1, "https://imdb.test/dune84", "Dune (1984)"), 1),
            Seq(ReferentStoreTests.Page(2, "https://imdb.test/dune21", "Dune (2021)"), 2)],
            Active(activeTab, activeTab == 1 ? "https://imdb.test/dune84" : "https://imdb.test/dune21"));

    [Fact]
    public async Task UniqueCurrentReferent_ResolvesWithoutAnyModel()
    {
        var candidates = ReferentResolver.BuildCandidates([Seq(ReferentStoreTests.Page(1, "https://a.test/x"), 1)], Active(1, "https://a.test/x"));
        var chooser = new Chooser(_ => throw new InvalidOperationException());
        var result = await ReferentResolver.ResolveAsync("open that", candidates, chooser);
        Assert.Equal(ReferentResolutionKind.Selected, result.Kind);
        Assert.Equal(0, chooser.Calls);
    }

    [Fact]
    public async Task UniqueNonCurrentReferent_StillNeedsTheFocusedPick()
    {
        var candidates = ReferentResolver.BuildCandidates([Seq(ReferentStoreTests.Page(1, "https://a.test/x"), 1)], Active(9, "https://z.test/"));
        var chooser = new Chooser(c => new(ReferentChoiceKind.None));
        var result = await ReferentResolver.ResolveAsync("open the docs", candidates, chooser);
        Assert.Equal(ReferentResolutionKind.None, result.Kind);
        Assert.Equal(1, chooser.Calls);
    }

    [Fact]
    public async Task TwoCandidates_PickerChoosesByTypedLabels()
    {
        var chooser = new Chooser(c => new(ReferentChoiceKind.Selected, c.Single(x => x.Referent.Label == "Dune (2021)").Id, ReferenceRelation.Property));
        var result = await ReferentResolver.ResolveAsync("the 2021 one", Two(), chooser);
        Assert.Equal("Dune (2021)", result.Candidate!.Referent.Label);
        Assert.Equal(2, chooser.Offered.Count);
    }

    [Fact]
    public async Task AmbiguousPick_IsAmbiguous_NotAGuess()
    {
        var result = await ReferentResolver.ResolveAsync("that one", Two(), new Chooser(_ => new(ReferentChoiceKind.Ambiguous)));
        Assert.Equal(ReferentResolutionKind.Ambiguous, result.Kind);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public async Task NonePick_FallsBackSafely()
    {
        var result = await ReferentResolver.ResolveAsync("open something new", Two(), new Chooser(_ => new(ReferentChoiceKind.None)));
        Assert.Equal(ReferentResolutionKind.None, result.Kind);
    }

    [Fact]
    public async Task PickOfAnUnofferedId_IsRejected()
    {
        var result = await ReferentResolver.ResolveAsync("that", Two(), new Chooser(_ => new(ReferentChoiceKind.Selected, "ref_99")));
        Assert.Equal(ReferentResolutionKind.Unavailable, result.Kind);
    }

    [Fact]
    public async Task Other_ResolvesToTheNonCurrentMemberOfARepresentedContrast()
    {
        // Tab 1 (Dune 1984) is in view; the contrast is Dune 2021.
        var chooser = new Chooser(c => new(ReferentChoiceKind.Selected, c.Single(x => !x.Current).Id, ReferenceRelation.Alternative));
        var result = await ReferentResolver.ResolveAsync("the other one", Two(1), chooser);
        Assert.Equal("Dune (2021)", result.Candidate!.Referent.Label);
    }

    [Fact]
    public async Task Other_NeverDenotesTheThingInView()
    {
        var chooser = new Chooser(c => new(ReferentChoiceKind.Selected, c.Single(x => x.Current).Id, ReferenceRelation.Alternative));
        var result = await ReferentResolver.ResolveAsync("the other one", Two(1), chooser);
        Assert.Equal(ReferentResolutionKind.Ambiguous, result.Kind);
    }

    [Fact]
    public async Task Other_WithoutARepresentedContrast_Clarifies()
    {
        // Two referents, but on different sites: no established pair to be "the other" of.
        var candidates = ReferentResolver.BuildCandidates([
            Seq(ReferentStoreTests.Page(1, "https://a.test/x", "A"), 1),
            Seq(ReferentStoreTests.Page(2, "https://b.test/y", "B"), 2)], Active(1, "https://a.test/x"));
        var chooser = new Chooser(c => new(ReferentChoiceKind.Selected, c.Single(x => !x.Current).Id, ReferenceRelation.Alternative));
        var result = await ReferentResolver.ResolveAsync("the other one", candidates, chooser);
        Assert.Equal(ReferentResolutionKind.Ambiguous, result.Kind);
        Assert.Equal("no_represented_contrast", result.Reason);
    }

    [Fact]
    public async Task NoReferents_NeverBuildsAContrastFromOpenTabs()
    {
        var chooser = new Chooser(_ => throw new InvalidOperationException());
        var candidates = ReferentResolver.BuildCandidates([], Active(1, "https://a.test/"));
        Assert.Empty(candidates);
        var result = await ReferentResolver.ResolveAsync("the other one", candidates, chooser);
        Assert.Equal(ReferentResolutionKind.Unavailable, result.Kind);
        Assert.Equal(0, chooser.Calls);
    }

    [Fact]
    public void ItemIsSupersededByALivePageShowingIt()
    {
        var candidates = ReferentResolver.BuildCandidates([
            Seq(ReferentStoreTests.Item("https://imdb.test/dune21", "Dune (2021)"), 1),
            Seq(ReferentStoreTests.Page(2, "https://imdb.test/dune21", "Dune (2021)"), 2)], null);
        Assert.Equal(ReferentKind.Page, Assert.Single(candidates).Referent.Kind);
    }

    [Fact]
    public void ItemWithoutALivePage_IsOffered_AndAppWindowsAndBareTabsAreNot()
    {
        var candidates = ReferentResolver.BuildCandidates([
            Seq(ReferentStoreTests.Item("https://imdb.test/dune84", "Dune (1984)"), 1),
            Seq(ReferentStoreTests.Window(5), 2),
            Seq(ReferentStoreTests.Page(1, "https://x.test/") with { Kind = ReferentKind.BrowserTab }, 3)], null);
        Assert.Equal(ReferentKind.Item, Assert.Single(candidates).Referent.Kind);
    }

    [Fact]
    public void Candidates_AreBoundedAndRecentFirst()
    {
        var referents = Enumerable.Range(1, 20).Select(i => Seq(ReferentStoreTests.Item($"https://a.test/{i}"), i)).ToArray();
        var candidates = ReferentResolver.BuildCandidates(referents, null);
        Assert.Equal(ReferentResolver.MaxCandidates, candidates.Count);
        Assert.Equal("https://a.test/20", candidates[0].Referent.Href);
        Assert.Equal("ref_1", candidates[0].Id);
    }
}
