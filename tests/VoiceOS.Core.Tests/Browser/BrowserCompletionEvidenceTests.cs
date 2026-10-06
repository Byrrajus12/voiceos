using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

/// <summary>
/// F4 browser end-state correctness. Normalizations below are the shapes the live normalizer
/// returned for the eval utterances; pages are the end states those runs reached.
/// </summary>
public sealed class BrowserCompletionEvidenceTests
{
    // ---------- Typed evidence ----------

    [Fact]
    public void SurfaceReady_OnRequestedSite_IsConfirmed_ButSearchEngineIsNot()
    {
        var goal = StackOverflow();
        Assert.Equal(CompletionEvidenceStrength.Confirmed, Evaluate(goal, "https://stackoverflow.com/", "Stack Overflow"));
        Assert.Equal(CompletionEvidenceStrength.Confirmed,
            Evaluate(goal, "https://stackoverflow.com/questions", "Highest scored questions - Stack Overflow"));
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(goal, "https://www.google.com/search?q=stack+overflow", "stack overflow - Google Search"));
    }

    [Fact]
    public void SurfaceReady_WithASpecificSubResourceType_IsNotDeterministic()
    {
        var goal = Goal("Open Stack Overflow jobs", SemanticEndState.SurfaceReady, entity: "Stack Overflow",
            resourceType: "jobs board", service: "Stack Overflow", serviceUrl: "https://stackoverflow.com");
        Assert.Equal(CompletionEvidenceStrength.Strong, Evaluate(goal, "https://stackoverflow.com/", "Stack Overflow"));
    }

    [Fact]
    public void SubResourceOnSpaRoot_IsUnestablished_NotContradictory_AndOpenedResourceControlSupports()
    {
        var goal = ShakeShackMenu();
        const string root = "https://shakeshack.com/";
        const string title = "Shake Shack: Burgers, Chicken, Fries, Shakes";
        // Root URL/title alone neither confirms the menu nor proves it absent.
        Assert.Equal(CompletionEvidenceStrength.Unestablished, Evaluate(goal, root, title));
        Assert.Equal(CompletionEvidenceStrength.Supporting, BrowserCompletionEvidence.Evaluate(goal,
            new BrowserPageFacts(root, title, OpenedControls: ["Open menu categories", "Burgers & More category"])).Strength);
        // An opened control unrelated to the requested resource is not evidence for it.
        Assert.Equal(CompletionEvidenceStrength.Unestablished, BrowserCompletionEvidence.Evaluate(goal,
            new BrowserPageFacts(root, title, OpenedControls: ["Accept cookies", "Order now"])).Strength);
        Assert.Equal(CompletionEvidenceStrength.Supporting, Evaluate(goal, "https://shakeshack.com/menu", "Menu | Shake Shack"));
        Assert.Equal(CompletionEvidenceStrength.Neutral, Evaluate(goal, "https://shakeshack.com/locations", "Locations | Shake Shack"));
    }

    [Fact]
    public void SubResourceOnNamedServiceRoot_IsUnestablished()
    {
        var goal = Goal("What's Delta's checked bag policy? Pull up their page on it.", SemanticEndState.ResourceOpened,
            entity: "Delta Air Lines", resourceType: "checked baggage policy page", service: "Delta Air Lines",
            serviceUrl: "https://www.delta.com");
        Assert.Equal(CompletionEvidenceStrength.Unestablished, Evaluate(goal, "https://www.delta.com/", "Delta Air Lines"));
        Assert.Equal(CompletionEvidenceStrength.Strong,
            Evaluate(goal, "https://www.delta.com/us/en/baggage/checked-baggage", "Checked Baggage : Delta Air Lines"));
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(goal, "https://www.delta.com/us/en/flight-deals", "Flight Deals : Delta Air Lines"));
    }

    [Fact]
    public void SiteItselfResource_AtRoot_IsOnlySupporting()
    {
        var goal = Goal("Take me to Hacker News.", SemanticEndState.ResourceOpened, entity: "Hacker News",
            resourceType: "website", service: "Hacker News", serviceUrl: "https://news.ycombinator.com");
        Assert.Equal(CompletionEvidenceStrength.Supporting, Evaluate(goal, "https://news.ycombinator.com/", "Hacker News"));
    }

    [Fact]
    public void ResultsVisible_NeedsQueryOnTheRequestedSite()
    {
        var imdb = Goal("Search IMDb for Dune.", SemanticEndState.ResultsVisible, entity: "Dune",
            resourceType: "movie or title", service: "IMDb", serviceUrl: "https://www.imdb.com", queries: ["Dune IMDb"]);
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(imdb, "https://www.google.com/search?q=Dune+IMDb", "Dune IMDb - Google Search"));
        Assert.Equal(CompletionEvidenceStrength.Strong, Evaluate(imdb, "https://www.imdb.com/find/?q=Dune", "Find - IMDb"));

        var open = Goal("Search for Dune reviews.", SemanticEndState.ResultsVisible, entity: "Dune",
            resourceType: "reviews", queries: ["Dune reviews"]);
        Assert.Equal(CompletionEvidenceStrength.Strong,
            Evaluate(open, "https://www.google.com/search?q=Dune+reviews&sca=1", "Dune reviews - Google Search"));
        Assert.Equal(CompletionEvidenceStrength.Neutral, Evaluate(open, "https://www.google.com/", "Google"));
    }

    [Fact]
    public void ResultsVisible_OnNamedHostListing_IsSupporting()
    {
        Assert.Equal(CompletionEvidenceStrength.Supporting, Evaluate(NvidiaJobs(),
            "https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite", "Search for Jobs"));
    }

    [Fact]
    public void ExactResourceOnRequestedSite_IsStrong_ButASiblingResourceIsNot()
    {
        var goal = DuneTitle();
        Assert.Equal(CompletionEvidenceStrength.Strong,
            Evaluate(goal, "https://www.imdb.com/title/tt1160419/", "Dune: Part One (2021) - IMDb"));
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(goal, "https://www.imdb.com/title/tt15239678/", "Dune: Part Two (2024) - IMDb"));
    }

    [Theory]
    [InlineData(SemanticEndState.ContentActive)]
    [InlineData(SemanticEndState.StateChanged)]
    [InlineData(SemanticEndState.OtherBoundedGoal)]
    public void StateGoals_StayModelJudged_EvenOnTheRequestedOrigin(SemanticEndState endState)
    {
        var goal = Goal("Play some lo-fi beats to study to.", endState, entity: "YouTube",
            service: "YouTube", serviceUrl: "https://www.youtube.com");
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(goal, "https://www.youtube.com/watch?v=jfKfPfyJRdk", "lofi hip hop radio - YouTube"));
    }

    [Fact]
    public void DestinationMatchIsDirectional()
    {
        var gmail = BrowserGoal.FromUtterance("Open Gmail") with
        {
            NamedServiceHint = "Gmail",
            Normalization = Normalization(SemanticEndState.SurfaceReady, entity: "Gmail", service: "Gmail")
        };
        Assert.Equal(CompletionEvidenceStrength.Neutral, Evaluate(gmail, "https://www.google.com/", "Google"));
        Assert.Equal(CompletionEvidenceStrength.Confirmed, Evaluate(gmail, "https://mail.google.com/mail/u/0/", "Inbox"));
        Assert.True(BrowserCompletionEvidence.IsSameSiteOrSubdomain("en.wikipedia.org", "www.wikipedia.org"));
    }

    [Fact]
    public void ThresholdsAreTieredAndNeverLooserThanConfigured()
    {
        Assert.Equal(.40, new BrowserCompletionEvidence(CompletionEvidenceStrength.Strong, "").Threshold(.55));
        Assert.Equal(.45, new BrowserCompletionEvidence(CompletionEvidenceStrength.Supporting, "").Threshold(.55));
        Assert.Equal(.55, new BrowserCompletionEvidence(CompletionEvidenceStrength.Neutral, "").Threshold(.55));
        Assert.Equal(.30, new BrowserCompletionEvidence(CompletionEvidenceStrength.Strong, "").Threshold(.30));
        Assert.Equal(0, new BrowserCompletionEvidence(CompletionEvidenceStrength.Confirmed, "").Threshold(.55));
        Assert.False(1.0 >= new BrowserCompletionEvidence(CompletionEvidenceStrength.Unestablished, "").Threshold(.55));
    }

    // Companion JSON as a pre-cleanup content script still sends it: the watch page's <video> is
    // dormant (paused, nothing loaded). Unknown fields are ignored and nothing waits on them.
    private const string DormantWatchPage = """
        {"tabId":4,"sessionId":"session","revision":"r1","url":"https://www.youtube.com/watch?v=abc",
         "title":"Travis Scott - FE!N - YouTube","visibleText":"","truncated":false,
         "viewport":{"width":1280,"height":800,"scrollX":0,"scrollY":0},
         "elements":[{"ref":"e1","role":"button","name":"Guide","enabled":true,"editable":false,
           "geometry":{"x":0,"y":0,"width":10,"height":10,"inViewport":true},"context":""}],
         "media":{"count":1,"playing":false,"items":[{"paused":true,"ended":false,"currentTime":0,"readyState":0,"live":false}]}}
        """;

    [Fact]
    public async Task ContentSelection_ObservesAndActsWithoutAnyMediaStartupDelay()
    {
        var snapshot = JsonSerializer.Deserialize<BrowserSnapshot>(DormantWatchPage, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var transport = new CountingTransport(snapshot);
        var goal = TravisScott();
        var surface = new BrowserSurface(transport, goal,
            new TypeSafeBrowserDecisionSource(new FakeGateway(_ => new Dictionary<string, JevAnswer>(), 0), goal), "session", tabId: 4);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var observation = await surface.ObserveAsync();
        await surface.ExecuteAsync(observation.Candidates.First(c => c.Id == "e1").Actions[0], observation);
        await surface.ObserveAfterActionAsync();
        await surface.ObserveAsync();
        clock.Stop();
        Assert.Equal(2, transport.Observations); // one per requested observation, never an extra settle
        Assert.Equal(1, transport.Actions);      // the action result is reused as its observation
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"observation stalled for {clock.Elapsed}");
        Assert.DoesNotContain("media", observation.Evidence);
    }

    private sealed class CountingTransport(BrowserSnapshot snapshot) : IChromeCompanionTransport
    {
        public int Observations { get; private set; }
        public int Actions { get; private set; }
        public ValueTask<BrowserSnapshot> OpenTaskTabAsync(string sessionId, string url, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();
        public ValueTask<BrowserSnapshot> ObserveAsync(string sessionId, int tabId, CancellationToken cancellationToken = default)
        {
            Observations++;
            return ValueTask.FromResult(snapshot);
        }
        public ValueTask<BrowserSnapshot> ActAsync(BrowserActionRequest action, CancellationToken cancellationToken = default)
        {
            Actions++;
            return ValueTask.FromResult(snapshot with { Revision = "r2" });
        }
    }

    // ---------- Completion / progress decisions over the real engine ----------

    [Fact]
    public async Task ExactResourceReached_CompletesDespiteBorderlineConfirmation()
    {
        var page = Page("https://www.imdb.com/title/tt1160419/", "Dune: Part One (2021) - IMDb", Click("Cast"));
        var gateway = Gateway(_ => (Operation: "DONE", Achieved: .54), confirm: .54);
        var result = await Run(DuneTitle(), gateway, page);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);

        // The same borderline judgment without typed resource evidence is not enough.
        var untyped = await Run(DuneTitle() with { Normalization = null }, Gateway(_ => ("DONE", .54), confirm: .54), page);
        Assert.NotEqual(InteractionCompletionState.Complete, untyped.Completion);
    }

    [Fact]
    public async Task SpaRoot_WithoutAnyResourceEvidence_DoesNotComplete()
    {
        var root = Page("https://shakeshack.com/", "Shake Shack: Burgers, Chicken, Fries, Shakes", Click("Menu"));
        var gateway = Gateway(_ => ("DONE", .67), confirm: .65);
        var result = await Run(ShakeShackMenu(), gateway, root);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
        Assert.DoesNotContain(gateway.Questions, questions => !questions.ContainsKey("operation"));
        Assert.Contains(gateway.States, state => state.GetProperty("correction").GetString()?.Contains("menu") == true);
    }

    [Fact]
    public async Task SubResourceRejection_LetsTheModelAdvanceToTheRequestedPage()
    {
        var root = Page("https://shakeshack.com/", "Shake Shack: Burgers, Chicken, Fries, Shakes", Click("Menu"));
        var menu = Page("https://shakeshack.com/menu", "Menu | Shake Shack", Click("Burgers"));
        var gateway = Gateway(state => Url(state) == "https://shakeshack.com/" && Correction(state) is null
            ? ("DONE", .67) : Url(state) == "https://shakeshack.com/" ? ("CLICK:Menu", .2) : ("DONE", .9), confirm: .9);
        var surface = new ScriptSurface(root, (_, _) => menu);
        var result = await Run(ShakeShackMenu(), gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["Menu"], surface.Clicked);
    }

    [Fact]
    public async Task SpaResourceOpenedInPage_CompletesOnTheRootUrl()
    {
        // Physical run: root URL/title never changed; the menu opened in place.
        const string root = "https://shakeshack.com/";
        const string title = "Shake Shack: Burgers, Chicken, Fries, Shakes";
        var surface = new ScriptSurface(Page(root, title, Click("Order now"), Click("Open menu categories")),
            (clicked, _) => clicked == "Open_menu_categories"
                ? Page(root, title, Click("Order now"), Click("Open menu categories"), Click("Burgers & More category"))
                : Page(root, title, Click("Order now"), Click("Burgers & More"), Click("Shakes")));
        var gateway = Gateway(state => (Correction(state), surface.Clicked.Count) switch
        {
            (null, 0) => ("DONE", .67), // bare landing: rejected, nothing shows the menu yet
            (_, 0) => ("CLICK:Open menu categories", .44),
            (_, 1) => ("CLICK:Burgers & More category", .68),
            _ => ("DONE", .80)
        }, confirm: .78);
        var result = await Run(ShakeShackMenu(), gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        // Physical goal_achieved was already 0.68 once the menu opened: the category click is unnecessary.
        Assert.Equal(["Open menu categories"], surface.Clicked);
    }

    [Fact]
    public async Task UnrelatedInPageClick_DoesNotEstablishTheRequestedResource()
    {
        const string root = "https://shakeshack.com/";
        var surface = new ScriptSurface(Page(root, "Shake Shack", Click("Accept cookies")),
            (_, _) => Page(root, "Shake Shack", Click("Order now")));
        var gateway = Gateway(_ => surface.Clicked.Count == 0 ? ("CLICK:Accept cookies", .3) : ("DONE", .8), confirm: .8);
        var result = await Run(ShakeShackMenu(), gateway, surface);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public async Task SurfaceReadySiteReached_CompletesWithoutPostArrivalClicks_OrAModelThreshold()
    {
        // Physical run: /questions, goal_achieved 0.33, the model proposed clicking Home.
        var page = Page("https://stackoverflow.com/questions", "Highest scored questions - Stack Overflow",
            Click("Stack Overflow Home"), Click("Home"));
        var gateway = Gateway(_ => ("CLICK:Stack Overflow Home", .33), confirm: .1);
        var surface = new ScriptSurface(page);
        var result = await Run(StackOverflow(), gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
        Assert.DoesNotContain(gateway.Questions, questions => !questions.ContainsKey("operation"));
    }

    [Fact]
    public async Task NvidiaPhysicalRun_SupportingEvidencePreemptsScroll_AndCompletes()
    {
        var goal = Goal("Show me where I can see open jobs at Nvidia.", SemanticEndState.ResultsVisible,
            entity: "NVIDIA", resourceType: "open jobs", service: "NVIDIA Careers", queries: ["NVIDIA careers open jobs"]);
        var page = Page("https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite", "Search for Jobs", Click("Filter"));
        Assert.Equal(CompletionEvidenceStrength.Supporting,
            Evaluate(goal, "https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite", "Search for Jobs"));
        var surface = new ScriptSurface(page);
        var result = await Run(goal, Gateway(_ => ("SCROLL_DOWN", .59), confirm: .63), surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
    }

    [Fact]
    public async Task ContentActive_CompletesOnSemanticJudgment_WithoutPlaybackProof()
    {
        // The requested video is open (its <video> may be paused or not yet loaded): no playback gate.
        var watch = Page("https://www.youtube.com/watch?v=abc", "Travis Scott - FE!N - YouTube", Click("Guide"));
        var surface = new ScriptSurface(watch);
        var result = await Run(TravisScott(), Gateway(_ => ("DONE", .9), confirm: .88), surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
    }

    [Fact]
    public async Task ContentActive_WatchUrlAlone_IsNotAutomaticSuccess()
    {
        // A watch page the model does not judge to be the requested content keeps the neutral bar.
        var watch = Page("https://www.youtube.com/watch?v=xyz", "Some other video - YouTube", Click("Guide"));
        Assert.Equal(CompletionEvidenceStrength.Neutral,
            Evaluate(TravisScott(), "https://www.youtube.com/watch?v=xyz", "Some other video - YouTube"));
        var result = await Run(TravisScott(), Gateway(_ => ("DONE", .3), confirm: .3), new ScriptSurface(watch));
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public async Task AlreadySatisfiedGoal_StopsInsteadOfClickingHome()
    {
        var page = Page("https://stackoverflow.com/", "Stack Overflow - Where Developers Learn", Click("Home"), Click("Questions"));
        var gateway = Gateway(_ => ("CLICK:Home", .61), confirm: .57);
        var surface = new ScriptSurface(page);
        var result = await Run(StackOverflow(), gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
    }

    [Fact]
    public async Task ResultsVisibleGoal_CompletesOnResults_WithoutOpeningAResult()
    {
        var goal = Goal("Search for Dune reviews.", SemanticEndState.ResultsVisible, entity: "Dune",
            resourceType: "reviews", queries: ["Dune reviews"]);
        var results = Page("https://www.google.com/search?q=Dune+reviews", "Dune reviews - Google Search",
            Click("Dune review - Rotten Tomatoes"));
        var surface = new ScriptSurface(results);
        var result = await Run(goal, Gateway(_ => ("CLICK:Dune review - Rotten Tomatoes", .5), confirm: .48), surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
    }

    [Fact]
    public async Task SurfaceReadyGoal_CompletesOnArrival_WithoutFurtherExploration()
    {
        var goal = Goal("Go to weather dot com.", SemanticEndState.SurfaceReady, entity: "The Weather Channel",
            resourceType: "website", service: "weather.com", serviceUrl: "https://weather.com");
        var serp = Page("https://www.google.com/search?q=weather.com", "weather.com - Google Search", Click("The Weather Channel"));
        var home = Page("https://weather.com/", "National and Local Weather Radar", Click("Radar"), Click("Video"));
        var surface = new ScriptSurface(serp, (_, _) => home);
        var gateway = Gateway(state => Url(state) == "https://weather.com/" ? ("CLICK:Radar", .5) : ("CLICK:The Weather Channel", .02),
            confirm: .5);
        var result = await Run(goal, gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["The Weather Channel"], surface.Clicked);
    }

    [Fact]
    public async Task ListingOnNamedHost_CompletesInsteadOfEndingOnAnUnsureOperation()
    {
        var page = Page("https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite", "Search for Jobs", Click("Filter"));
        var gateway = Gateway(_ => ("SCROLL_DOWN", .54), confirm: .53, operationConfidence: .33);
        var result = await Run(NvidiaJobs(), gateway, new ScriptSurface(page));
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
    }

    [Fact]
    public async Task NeutralEvidence_KeepsTheOriginalThreshold_AndStillActs()
    {
        var goal = Goal("Click on running backs", SemanticEndState.StateChanged, entity: "Running Backs");
        var page = Page("https://team.test/roster", "Roster", Click("Running Backs"));
        var after = Page("https://team.test/roster", "Roster", Click("Running Backs"), Click("Player A"));
        var surface = new ScriptSurface(page, (_, _) => after);
        var gateway = Gateway(state => surface.Clicked.Count == 0 ? ("CLICK:Running Backs", .54) : ("DONE", .9), confirm: .9);
        var result = await Run(goal, gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["Running Backs"], surface.Clicked);
    }

    [Fact]
    public async Task ClickWithoutSemanticEndState_IsNotAutomaticallyComplete()
    {
        var goal = Goal("Select dark theme", SemanticEndState.StateChanged, entity: "Dark theme");
        var page = Page("https://app.test/settings", "Settings", Click("Appearance"));
        var after = Page("https://app.test/settings", "Settings", Click("Appearance"), Click("Light"));
        var surface = new ScriptSurface(page, (_, _) => after);
        var gateway = Gateway(_ => surface.Clicked.Count == 0 ? ("CLICK:Appearance", .1) : ("DONE", .3), confirm: .3);
        var result = await Run(goal, gateway, surface);
        Assert.NotEqual(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["Appearance"], surface.Clicked);
    }

    [Fact]
    public async Task RepeatedSameControlOnSamePage_StopsSafelyInsteadOfLooping()
    {
        var goal = Goal("Play some lo-fi beats to study to.", SemanticEndState.ContentActive,
            entity: "Lo-fi study beats", resourceType: "Music playlist or stream", queries: ["lo-fi beats to study to"]);
        const string url = "https://www.youtube.com/watch?v=jfKfPfyJRdk";
        var toggles = 0;
        var surface = new ScriptSurface(Page(url, "lofi hip hop radio - YouTube", Click("Guide")),
            (_, _) => Page(url, "lofi hip hop radio - YouTube", [Click("Guide"), .. Enumerable.Range(0, ++toggles % 2 * 3)
                .Select(index => Click($"Subscription {index}"))]));
        var result = await Run(goal, Gateway(_ => ("CLICK:Guide", .5), confirm: .5), surface);
        Assert.Equal(InteractionCompletionState.Uncertain, result.Completion);
        Assert.Equal(["Guide"], surface.Clicked);
        Assert.Equal(["complete", "continue", "cancel"], result.Choices!.Select(choice => choice.Id));
    }

    [Fact]
    public async Task SameControlFromANewPage_IsNotARepeat()
    {
        var goal = Goal("Show me page three of the results", SemanticEndState.OtherBoundedGoal, entity: "results page 3");
        var surface = new ScriptSurface(Page("https://list.test/?page=1", "Results", Click("Next")),
            (_, count) => Page($"https://list.test/?page={count + 1}", "Results", Click("Next")));
        var gateway = Gateway(state => Url(state).EndsWith("page=3") ? ("DONE", .9) : ("CLICK:Next", .1), confirm: .9);
        var result = await Run(goal, gateway, surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Equal(["Next", "Next"], surface.Clicked);
    }

    [Fact]
    public async Task ExactResourceAlreadyPresent_CompletesWithoutTheProposedAction()
    {
        var page = Page("https://www.imdb.com/title/tt1160419/", "Dune: Part One (2021) - IMDb", Click("Cast"), Click("Trivia"));
        var surface = new ScriptSurface(page);
        var result = await Run(DuneTitle(), Gateway(_ => ("CLICK:Cast", .46), confirm: .5), surface);
        Assert.Equal(InteractionCompletionState.Complete, result.Completion);
        Assert.Empty(surface.Clicked);
    }

    // ---------- Fixtures ----------

    private static BrowserGoal StackOverflow() => Goal("Pull up Stack Overflow.", SemanticEndState.SurfaceReady,
        entity: "Stack Overflow", resourceType: "website", service: "Stack Overflow", serviceUrl: "https://stackoverflow.com");
    private static BrowserGoal ShakeShackMenu() => Goal("Find the menu for Shake Shack.", SemanticEndState.ResourceLocated,
        entity: "Shake Shack", resourceType: "menu", queries: ["Shake Shack menu"]);
    private static BrowserGoal TravisScott() => Goal("Play Travis Scott on YouTube.", SemanticEndState.ContentActive,
        entity: "Travis Scott", resourceType: "music video", service: "YouTube", serviceUrl: "https://www.youtube.com",
        queries: ["Travis Scott"]);
    private static BrowserGoal NvidiaJobs() => Goal("Show me where I can see open jobs at Nvidia.", SemanticEndState.ResultsVisible,
        entity: "NVIDIA", resourceType: "Open job listings", service: "NVIDIA Careers", queries: ["NVIDIA careers open jobs"]);
    private static BrowserGoal DuneTitle() => Goal("Open Dune 2021 on IMDb.", SemanticEndState.ResourceOpened,
        entity: "Dune (2021 film)", resourceType: "IMDb title page", service: "IMDb", serviceUrl: "https://www.imdb.com",
        queries: ["Dune 2021 IMDb"]);

    private static BrowserGoal Goal(string utterance, SemanticEndState endState, string? entity = null,
        string? resourceType = null, string? service = null, string? serviceUrl = null, string[]? queries = null)
        => BrowserGoal.FromUtterance(utterance) with
        {
            Normalization = Normalization(endState, entity, resourceType, service, serviceUrl, queries)
        };

    private static BrowserGoalNormalization Normalization(SemanticEndState endState, string? entity = null,
        string? resourceType = null, string? service = null, string? serviceUrl = null, string[]? queries = null)
        => new("objective", entity, resourceType, service, serviceUrl, queries ?? [], "hint", [], endState);

    private static CompletionEvidenceStrength Evaluate(BrowserGoal goal, string url, string title)
        => BrowserCompletionEvidence.Evaluate(goal, url, title).Strength;

    private static InteractionCandidate Click(string name) => new(Id(name), $"link '{name}' value='' context=''",
        [new($"click-{Id(name)}", InteractionActionKind.Activate, Id(name))]);
    private static string Id(string name) => name.Replace(' ', '_');

    /// <summary>Same evidence shape as BrowserSurface: elements carry id/Role/Name.</summary>
    private static InteractionObservation Page(string url, string title, params InteractionCandidate[] candidates)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            current_url = url, current_title = title,
            elements = candidates.Select(candidate => new { id = candidate.Id, Role = "link", Name = candidate.Id.Replace('_', ' ') })
        });
        return new(0, evidence, evidence, [.. candidates, new("page", "Current page",
            [new("scroll-down", InteractionActionKind.Scroll, Direction: "down")])]);
    }

    private static string Url(JsonElement state) => JsonDocument.Parse(state.GetProperty("fresh_dom_observation").GetString()!)
        .RootElement.GetProperty("current_url").GetString()!;
    private static string? Correction(JsonElement state) => state.GetProperty("correction").GetString();

    private static async Task<InteractionRunResult> Run(BrowserGoal goal, FakeGateway gateway, InteractionObservation page)
        => await Run(goal, gateway, new ScriptSurface(page));

    private static async Task<InteractionRunResult> Run(BrowserGoal goal, FakeGateway gateway, ScriptSurface surface)
    {
        var source = new TypeSafeBrowserDecisionSource(gateway, goal);
        surface.Completion = source;
        return await new InteractionEngine().RunAsync(new(goal.OriginalUtterance), surface, source,
            new InteractionBudget(12, 8, 3, 30));
    }

    /// <summary>Operation answers are "DONE", "SCROLL_DOWN", or "CLICK:&lt;name&gt;"; confirmation
    /// requests (no operation head) answer goal_achieved with <paramref name="confirm"/>.</summary>
    private static FakeGateway Gateway(Func<JsonElement, (string Operation, double Achieved)> decide,
        double confirm, double operationConfidence = .99)
        => new(state =>
        {
            var (operation, achieved) = decide(state);
            var target = operation.StartsWith("CLICK:", StringComparison.Ordinal) ? Id(operation[6..]) : null;
            var answers = new Dictionary<string, JevAnswer>
            {
                ["operation"] = new("choice", target is null ? operation : "CLICK",
                    new Dictionary<string, double> { [target is null ? operation : "CLICK"] = operationConfidence }, operationConfidence),
                ["goal_achieved"] = Noul(achieved),
                ["stuck"] = Noul(.1)
            };
            if (target is not null)
                answers["click_target"] = new("choice", target, new Dictionary<string, double> { [target] = .99 }, .99);
            return answers;
        }, confirm);

    private static JevAnswer Noul(double value) => new("noul", value >= .5 ? "true" : "false",
        new Dictionary<string, double> { ["noul"] = value }, value);

    private sealed class FakeGateway(Func<JsonElement, IReadOnlyDictionary<string, JevAnswer>> decide, double confirm) : IJevGateway
    {
        public List<JsonElement> States { get; } = [];
        public List<IReadOnlyDictionary<string, JevQuestionDto>> Questions { get; } = [];
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            var element = JsonSerializer.SerializeToElement(state);
            States.Add(element);
            Questions.Add(questions);
            return Task.FromResult(questions.ContainsKey("operation") ? decide(element)
                : (IReadOnlyDictionary<string, JevAnswer>)new Dictionary<string, JevAnswer> { ["goal_achieved"] = Noul(confirm) });
        }
    }

    /// <summary>Replays a page and, per click, the page the click produced (label, clicks so far → next page).</summary>
    private sealed class ScriptSurface(InteractionObservation first,
        Func<string, int, InteractionObservation>? afterClick = null) : IInteractionSurface
    {
        private InteractionObservation _current = first;
        private long _revision;
        public TypeSafeBrowserDecisionSource? Completion { get; set; }
        public List<string> Clicked { get; } = [];

        public ValueTask<InteractionObservation> ObserveAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_current = _current with { Revision = ++_revision });

        public ValueTask<InteractionActionResult> ExecuteAsync(InteractionAction action,
            InteractionObservation observation, CancellationToken cancellationToken = default)
        {
            Clicked.Add(action.TargetId?.Replace('_', ' ') ?? action.Kind.ToString());
            if (action.Kind != InteractionActionKind.Activate || afterClick is null)
                throw new InvalidOperationException($"Unexpected action {action.Kind} {action.TargetId}");
            _current = afterClick(action.TargetId!, Clicked.Count);
            return ValueTask.FromResult(InteractionActionResult.Ok());
        }

        public ValueTask<InteractionCompletionAssessment> AssessCompletionAsync(InteractionGoal goal,
            InteractionObservation observation, IReadOnlyList<InteractionHistoryEntry> recentHistory,
            CancellationToken cancellationToken = default)
            => Completion!.AssessAsync(BrowserGoal.FromUtterance(goal.Text), observation, recentHistory, cancellationToken);
    }
}
