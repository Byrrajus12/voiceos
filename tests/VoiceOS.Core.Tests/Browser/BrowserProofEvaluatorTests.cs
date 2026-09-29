using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// Table-driven proof rules. Every Proved row has a positive test and paired negatives that differ in
/// exactly one field; the MustNotProve set is zero-tolerance.
/// </summary>
public sealed class BrowserProofEvaluatorTests
{
    private const string Docs = "https://docs.example.com/guide";
    private static readonly InteractionAction Click = new("r3:click:e5", InteractionActionKind.Activate, "e5");

    private static BrowserGoal Goal(Uri? destination = null, string[]? queries = null, string? service = null, string? entity = null)
        => BrowserGoal.FromUtterance("u") with
        {
            ScopedDestination = destination,
            Normalization = new("objective", entity, null, service, null, queries ?? [], "hint", [], SemanticEndState.ResourceOpened)
        };

    private static OutcomeStep Step(ProofFamily family, bool reliable = true, string phrase = "the docs link", string? query = null)
        => new("s1", family, new(phrase, query), reliable);

    private static InteractionObservation Obs(string url, string title = "Page")
        => new(4, "k", JsonSerializer.Serialize(new { current_url = url, current_title = title }), []);

    private static TypedRef Ref(string element = "e5", long revision = 3, string? href = Docs, string role = "link", string label = "Docs")
        => new(7, "s", revision, element, "fp", label, role, href);

    private static Effect Activated(TypedRef? subject = null)
        => new(EffectKind.Activated, EffectSource.CompanionResponse, EffectStrength.Observed, subject ?? Ref());

    private static Effect Navigated(string from, string to)
        => new(EffectKind.Navigated, EffectSource.SnapshotDelta, EffectStrength.Derived, null,
            new Dictionary<string, string> { ["from"] = from, ["to"] = to });

    private static Effect Changed(bool subjectPresent)
        => new(EffectKind.ContentChanged, EffectSource.SnapshotDelta, EffectStrength.Derived, null,
            new Dictionary<string, string> { ["subjectPresent"] = subjectPresent ? "true" : "false" });

    private static Effect NoEffect()
        => new(EffectKind.NoEffect, EffectSource.EngineRule, EffectStrength.Derived, null,
            new Dictionary<string, string> { ["reason"] = "state_unchanged" });

    private static Effect Adopted(string url)
        => new(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["mode"] = "adopted", ["url"] = url });

    private static Effect Acquired(string url = "https://docs.example.com/")
        => new(EffectKind.SurfaceAcquired, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["mode"] = "opened", ["url"] = url });

    private static Effect TextSet(string value, bool matched = true)
        => new(EffectKind.TextSet, EffectSource.CompanionResponse, EffectStrength.Observed, null,
            new Dictionary<string, string> { ["value"] = value, ["matched"] = matched ? "true" : "false", ["mode"] = "replace" });

    private static IReadOnlyList<Effect> Ledger(params (Effect Effect, string? Action)[] items)
    {
        var ledger = new EffectLedger();
        foreach (var (effect, action) in items) ledger.Append([effect], action);
        return ledger.All;
    }

    private static TargetBinding Strong(string element = "e5", long revision = 3)
        => new(element, revision, BindingMethod.JevChoice, 12, .92, .81);

    private static TargetBinding Moderate() => new("e5", 3, BindingMethod.JevChoice, 12, .70, .30);
    private static TargetBinding Weak() => new("e5", 3, BindingMethod.JevChoice, 12, .55, .10);

    private static ProofVerdict Activate(IReadOnlyList<Effect> effects, TargetBinding? binding, string currentUrl = Docs,
        bool reliable = true, InteractionAction? action = null, BrowserGoal? goal = null, string title = "Guide", ProofThresholds? thresholds = null)
        => new BrowserProofEvaluator(goal ?? Goal(), thresholds).Evaluate(new(Step(ProofFamily.Activate, reliable), effects,
            action ?? Click, binding, Obs(currentUrl, title)));

    private static IReadOnlyList<Effect> LinkFollowed(string to = Docs, TypedRef? subject = null, string from = "https://docs.example.com/")
        => Ledger((Activated(subject), Click.Id), (Navigated(from, to), Click.Id));

    // ── Activate: positives ────────────────────────────────────────────────────

    [Fact]
    public void Activate_StrongBinding_ExactLink_Proves()
    {
        var verdict = Activate(LinkFollowed(), Strong());
        Assert.Equal(ProofStatus.Proved, verdict.Status);
        Assert.Equal("link_followed_exact", verdict.Rule);
    }

    [Fact]
    public void Activate_ExactLabelBinding_Proves()
        => Assert.Equal(ProofStatus.Proved, Activate(LinkFollowed(), new("e5", 3, BindingMethod.ExactLabel, 9)).Status);

    [Fact]
    public void Activate_EmbeddedRedirectorHref_Proves()
    {
        var href = "https://www.google.com/url?q=" + Uri.EscapeDataString("https://rust-lang.org/") + "&sa=U";
        var verdict = Activate(LinkFollowed("https://rust-lang.org/", Ref(href: href)), Strong(), "https://rust-lang.org/");
        Assert.Equal("link_followed_embedded", verdict.Rule);
    }

    [Fact]
    public void Activate_AdoptedTab_WithMatchingHref_Proves()
    {
        var effects = Ledger((Activated(), Click.Id), (Adopted(Docs), Click.Id));
        Assert.Equal(ProofStatus.Proved, Activate(effects, Strong()).Status);
    }

    [Fact]
    public void Activate_ModerateBinding_NeedsCorroboration()
    {
        var goal = Goal();
        var corroborated = Activate(LinkFollowed(), Moderate(), title: "Docs guide");
        // The phrase is "the docs link": "docs" is 4 letters and appears in the host and path.
        Assert.Equal(ProofStatus.Proved, corroborated.Status);
        var other = Activate(LinkFollowed("https://shop.example.net/checkout", Ref(href: "https://shop.example.net/checkout")),
            Moderate(), "https://shop.example.net/checkout", title: "Checkout", goal: goal);
        Assert.Equal(ProofStatus.Inconclusive, other.Status);
        Assert.Equal("moderate_binding_uncorroborated", other.Rule);
    }

    [Fact]
    public void Activate_HubPageThatDoesNotEchoTheControlsOwnWords_DoesNotProve()
    {
        // "READ THE BOOK" bound strongly, but it led to a "Learn" hub: the landing shows none of the control's distinctive word.
        var hub = "https://rust.example/learn";
        var effects = LinkFollowed(hub, Ref(href: hub, label: "READ THE BOOK"));
        var binding = new TargetBinding("e5", 3, BindingMethod.JevChoice, 12, .91, .85, "READ THE BOOK", LabelTerms: ["book"]);
        var verdict = Activate(effects, binding, hub, title: "Learn Rust - Rust Programming Language");
        Assert.Equal(ProofStatus.Inconclusive, verdict.Status);
        Assert.Equal("label_not_reflected", verdict.Rule);
        // Same evidence, but the landing is the book itself.
        var book = "https://rust.example/book/";
        Assert.Equal(ProofStatus.Proved, Activate(LinkFollowed(book, Ref(href: book)), binding, book, title: "The Rust Programming Language").Status);
        // The distinctive word may show in the title instead of the URL.
        Assert.Equal(ProofStatus.Proved, Activate(effects, binding, hub, title: "The Book").Status);
        // Generic labels contribute no distinctive terms, so nothing extra is required.
        var generic = new TargetBinding("e5", 3, BindingMethod.JevChoice, 12, .91, .85, "Details", LabelTerms: []);
        Assert.Equal(ProofStatus.Proved, Activate(effects, generic, hub, title: "Learn Rust").Status);
    }

    [Fact]
    public void Activate_ControlWithoutHref_NeverProves_EvenWhenItsOwnStateChanged()
    {
        // Live shadow data: a strongly bound button whose click changed the page was not the goal.
        var button = Ref(href: null, role: "button");
        foreach (var subjectPresent in new[] { true, false })
        {
            var effects = Ledger((Activated(button), Click.Id), (Changed(subjectPresent), Click.Id));
            var verdict = Activate(effects, Strong());
            Assert.Equal(ProofStatus.Inconclusive, verdict.Status);
            Assert.Equal("non_navigating_control", verdict.Rule);
        }
        Assert.Equal(ProofStatus.NotYet, Activate(Ledger((Activated(button), Click.Id)), Strong()).Status);
    }

    // ── Activate: must-not-prove ───────────────────────────────────────────────

    [Fact]
    public void Activate_ClickSuccessWithNoEffect_NeverProves()
    {
        var effects = Ledger((Activated(), Click.Id), (NoEffect(), Click.Id));
        Assert.Equal(ProofStatus.NotYet, Activate(effects, Strong()).Status);
    }

    [Fact]
    public void Activate_ActivationAloneWithoutConsequence_NeverProves()
    {
        var effects = Ledger((Activated(), Click.Id));
        Assert.NotEqual(ProofStatus.Proved, Activate(effects, Strong()).Status);
    }

    [Fact]
    public void Activate_HostMismatch_NeverProves_AndIsNeverRefuted()
    {
        // Wrong page, SSO interstitial and a legitimate short-link redirect look identical by host: Inconclusive, never Refuted.
        var verdict = Activate(LinkFollowed("https://login.other-site.test/sso", Ref(href: "https://docs.example.com/guide"), "https://start.example.org/"),
            Strong(), "https://login.other-site.test/sso");
        Assert.Equal(ProofStatus.Inconclusive, verdict.Status);
        Assert.Equal("inconsistent_destination", verdict.Rule);
    }

    [Fact]
    public void Activate_InconsistentButSameSiteHref_IsInconclusiveNeverRefuted()
    {
        var verdict = Activate(LinkFollowed("https://docs.example.com/login?next=%2Fguide", from: "https://docs.example.com/"), Strong(),
            "https://docs.example.com/login?next=%2Fguide");
        Assert.Equal(ProofStatus.Inconclusive, verdict.Status);
    }

    [Fact]
    public void Activate_SameOriginOnly_DoesNotProveUnlessAllowed()
    {
        var effects = LinkFollowed("https://docs.example.com/guide/intro");
        Assert.Equal("same_origin_only", Activate(effects, Strong(), "https://docs.example.com/guide/intro").Rule);
        Assert.Equal(ProofStatus.Proved, Activate(effects, Strong(), "https://docs.example.com/guide/intro",
            thresholds: new ProofThresholds(AllowSameOriginLink: true)).Status);
    }

    [Fact]
    public void Activate_StaleRevisionOrRef_NeverProves()
    {
        Assert.Equal("target_mismatch", Activate(LinkFollowed(), Strong(revision: 2)).Rule);
        Assert.Equal("target_mismatch", Activate(LinkFollowed(), Strong(element: "e6")).Rule);
    }

    [Fact]
    public void Activate_UnrelatedContentChange_NeverProves()
    {
        var effects = Ledger((Activated(Ref(href: null, role: "button")), Click.Id), (Changed(subjectPresent: true), Click.Id));
        Assert.NotEqual(ProofStatus.Proved, Activate(effects, Strong()).Status);
    }

    [Fact]
    public void Activate_LinkThatOnlyChangedContent_NeverProves()
    {
        var effects = Ledger((Activated(), Click.Id), (Changed(subjectPresent: false), Click.Id));
        Assert.Equal("link_without_navigation", Activate(effects, Strong()).Rule);
    }

    [Fact]
    public void Activate_AlreadyOnTargetUrl_NoNavigation_NeverProves()
    {
        var effects = Ledger((Activated(), Click.Id));
        Assert.NotEqual(ProofStatus.Proved, Activate(effects, Strong(), Docs).Status);
    }

    [Fact]
    public void Activate_WeakOrMissingBinding_NeverProves()
    {
        Assert.Equal("weak_binding", Activate(LinkFollowed(), Weak()).Rule);
        Assert.Equal(ProofStatus.NotYet, Activate(LinkFollowed(), null).Status);
        Assert.Equal("weak_binding", Activate(LinkFollowed(), new TargetBinding("e5", 3, BindingMethod.JevChoice, 3, null, null)).Rule);
    }

    [Fact]
    public void Activate_UnreliableDescriptor_CapsAtInconclusive_EvenWithPerfectEvidence()
        => Assert.Equal("descriptor_unreliable", Activate(LinkFollowed(), Strong(), reliable: false).Rule);

    [Fact]
    public void Activate_MeansActionMistakenForTarget_ButNavigationFromBareButton_NeverProves()
    {
        var search = Ref(href: null, role: "button", label: "Search");
        var effects = Ledger((Activated(search), Click.Id), (Navigated("https://a.example.com/", "https://a.example.com/results?q=x"), Click.Id));
        var verdict = Activate(effects, Strong(), "https://a.example.com/results?q=x");
        Assert.Equal(ProofStatus.Inconclusive, verdict.Status);
        Assert.Equal("non_navigating_control", verdict.Rule);
    }

    [Fact]
    public void Activate_NoActivationEffect_AsAfterTopologyOrStaleFailure_NeverProves()
    {
        Assert.NotEqual(ProofStatus.Proved, Activate(Ledger(), Strong()).Status);
        Assert.NotEqual(ProofStatus.Proved, Activate(Ledger((Navigated("https://a.test/", Docs), Click.Id)), Strong()).Status);
    }

    [Fact]
    public void Activate_EffectsOfADifferentAction_DoNotCount()
    {
        var effects = Ledger((Activated(), "r2:click:e5"), (Navigated("https://a.example.com/", Docs), "r2:click:e5"));
        Assert.NotEqual(ProofStatus.Proved, Activate(effects, Strong()).Status);
    }

    [Fact]
    public void Activate_NonActivateLastAction_IsNotAnActivation()
    {
        var scroll = new InteractionAction("r3:scroll:down", InteractionActionKind.Scroll, Direction: "down");
        Assert.Equal(ProofStatus.NotYet, Activate(LinkFollowed(), Strong(), action: scroll).Status);
        Assert.Equal(ProofStatus.NotYet, new BrowserProofEvaluator(Goal()).Evaluate(
            new(Step(ProofFamily.Activate), LinkFollowed(), null, Strong(), Obs(Docs))).Status);
    }

    // ── Surface ────────────────────────────────────────────────────────────────

    private static ProofVerdict Surface(IReadOnlyList<Effect> effects, string url, BrowserGoal? goal = null, InteractionAction? action = null)
        => new BrowserProofEvaluator(goal ?? Goal(new Uri("https://docs.example.com/"))).Evaluate(
            new(Step(ProofFamily.Surface), effects, action, null, Obs(url)));

    [Fact]
    public void Surface_AcquiredOnDestination_Proves()
    {
        Assert.Equal(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://docs.example.com/").Status);
        Assert.Equal(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://api.docs.example.com/x").Status);
    }

    [Fact]
    public void Surface_MustNotProve()
    {
        // wrong origin (SSO/consent host, lookalike, parent domain)
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://consent.other.test/").Status);
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://docs.example.com.evil.test/").Status);
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://example.com/").Status);
        // non-web content
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "about:blank").Status);
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "chrome://settings").Status);
        // implicit active tab: on the destination but never acquired
        Assert.Equal("no_acquisition", Surface(Ledger(), "https://docs.example.com/").Rule);
        // no known destination to compare against
        Assert.NotEqual(ProofStatus.Proved, Surface(Ledger((Acquired(), null)), "https://docs.example.com/", Goal()).Status);
        // once an action has run the initial acquisition no longer proves the outcome
        Assert.Equal(ProofStatus.NotYet, Surface(Ledger((Acquired(), null)), "https://docs.example.com/", action: Click).Status);
    }

    // ── Find ───────────────────────────────────────────────────────────────────

    private static readonly InteractionAction Type = new("r1:replace:e2", InteractionActionKind.SetText, "e2", "rust ownership");
    private static readonly InteractionAction Submit = new("r2:click:e9", InteractionActionKind.Activate, "e9");

    private static ProofVerdict Find(IReadOnlyList<Effect> effects, string url, InteractionAction? action = null,
        bool reliable = true, string title = "Search", string? query = "rust ownership", BrowserGoal? goal = null)
        => new BrowserProofEvaluator(goal ?? Goal(queries: ["rust ownership"])).Evaluate(
            new(Step(ProofFamily.Find, reliable, query: query), effects, action ?? Submit, null, Obs(url, title)));

    private static IReadOnlyList<Effect> Searched(string value = "rust ownership", bool matched = true, string to = "https://search.example.com/results?q=rust+ownership")
        => Ledger((TextSet(value, matched), Type.Id), (Activated(Ref("e9", 2, null, "button")), Submit.Id),
            (Navigated("https://search.example.com/", to), Submit.Id));

    [Fact]
    public void Find_TypedSubmittedAndShown_Proves()
    {
        var verdict = Find(Searched(), "https://search.example.com/results?q=rust+ownership");
        Assert.Equal(ProofStatus.Proved, verdict.Status);
        Assert.Equal("query_results_shown", verdict.Rule);
    }

    [Fact]
    public void Find_OpaqueUrl_ProvesOnlyWhenTheTitleNamesTheQuery()
    {
        var opaque = Searched(to: "https://search.example.com/r/8fa1c");
        Assert.Equal("query_results_titled", Find(opaque, "https://search.example.com/r/8fa1c", title: "rust ownership - Search").Rule);
        Assert.Equal("query_not_shown", Find(opaque, "https://search.example.com/r/8fa1c", title: "Search").Rule);
    }

    [Fact]
    public void Find_MustNotProve()
    {
        // typing without submission
        Assert.Equal(ProofStatus.NotYet, Find(Ledger((TextSet("rust ownership"), Type.Id)), "https://search.example.com/", Type).Status);
        // submit ok but nothing changed
        var noEffect = Ledger((TextSet("rust ownership"), Type.Id), (Activated(Ref("e9", 2, null, "button")), Submit.Id), (NoEffect(), Submit.Id));
        Assert.Equal(ProofStatus.NotYet, Find(noEffect, "https://search.example.com/").Status);
        // same-page content change only (URL opaque): not enough
        var changedOnly = Ledger((TextSet("rust ownership"), Type.Id), (Changed(true), Submit.Id));
        Assert.Equal(ProofStatus.Inconclusive, Find(changedOnly, "https://search.example.com/").Status);
        // query substituted/autocompleted: results for something else
        var substituted = Searched(to: "https://search.example.com/results?q=rust+ownership+quotes+from+the+book");
        Assert.Equal(ProofStatus.Proved, Find(substituted, "https://search.example.com/results?q=rust+ownership+quotes+from+the+book").Status);
        var different = Searched(to: "https://search.example.com/results?q=python+decorators");
        Assert.Equal(ProofStatus.Refuted, Find(different, "https://search.example.com/results?q=python+decorators").Status);
        // preexisting matching text without a causal navigation after the text was set
        Assert.NotEqual(ProofStatus.Proved, Find(Ledger((TextSet("rust ownership"), Type.Id), (Activated(Ref("e9", 2, null, "button")), Submit.Id)),
            "https://search.example.com/results?q=rust+ownership").Status);
        // value not from the grounded query
        Assert.Equal("query_not_grounded", Find(Searched("newsletter email"), "https://search.example.com/results?q=rust+ownership").Rule);
        // text not confirmed by readback
        Assert.Equal("text_not_confirmed", Find(Searched(matched: false), "https://search.example.com/results?q=rust+ownership").Rule);
        // query terms only match the site name
        var site = Goal(new Uri("https://search.example.com/"), ["search"]);
        var siteOnly = Ledger((TextSet("search"), Type.Id), (Activated(Ref("e9", 2, null, "button")), Submit.Id),
            (Navigated("https://search.example.com/", "https://search.example.com/results"), Submit.Id));
        Assert.Equal("query_only_site_words", Find(siteOnly, "https://search.example.com/results", query: "search", goal: site).Rule);
        // unreliable descriptor
        Assert.Equal("descriptor_unreliable", Find(Searched(), "https://search.example.com/results?q=rust+ownership", reliable: false).Rule);
        // wrong origin when a destination is known
        var known = Goal(new Uri("https://search.example.com/"), ["rust ownership"]);
        Assert.Equal("wrong_origin", Find(Searched(to: "https://elsewhere.test/results?q=rust+ownership"),
            "https://elsewhere.test/results?q=rust+ownership", goal: known).Rule);
    }

    [Fact]
    public void Find_TextTypedInAPriorAction_ButSubmitNavigationIsUnrelated_DoesNotProve()
    {
        var unrelated = Searched(to: "https://search.example.com/about");
        Assert.NotEqual(ProofStatus.Proved, Find(unrelated, "https://search.example.com/about").Status);
    }

    // ── Reach ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Reach_NeverProves_EvenWithEveryEffect()
    {
        var effects = Ledger((Acquired(), null), (Activated(), Click.Id), (Navigated("https://a.test/", Docs), Click.Id));
        var verdict = new BrowserProofEvaluator(Goal(new Uri("https://docs.example.com/"))).Evaluate(
            new(Step(ProofFamily.Reach), effects, Click, Strong(), Obs(Docs)));
        Assert.Equal(ProofStatus.NotYet, verdict.Status);
    }

    // ── properties ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnyLedgerWhoseLastActionHadNoEffect_NeverProves()
    {
        foreach (var family in new[] { ProofFamily.Find, ProofFamily.Activate })
        {
            var effects = Ledger((TextSet("rust ownership"), Type.Id), (Activated(), Click.Id), (Navigated("https://a.test/", Docs), Click.Id), (NoEffect(), Click.Id));
            var verdict = new BrowserProofEvaluator(Goal(queries: ["rust ownership"])).Evaluate(
                new(Step(family, query: "rust ownership"), effects, Click, Strong(), Obs(Docs)));
            Assert.NotEqual(ProofStatus.Proved, verdict.Status);
        }
    }

    [Fact]
    public void UnreliableDescriptor_NeverProves_ForFindOrActivate()
    {
        foreach (var family in new[] { ProofFamily.Find, ProofFamily.Activate })
        {
            var effects = Ledger((TextSet("rust ownership"), Type.Id), (Activated(), Click.Id), (Navigated("https://a.test/", Docs), Click.Id));
            var verdict = new BrowserProofEvaluator(Goal(queries: ["rust ownership"])).Evaluate(
                new(Step(family, reliable: false, query: "rust ownership"), effects, Click, Strong(), Obs(Docs)));
            Assert.NotEqual(ProofStatus.Proved, verdict.Status);
        }
    }
}
