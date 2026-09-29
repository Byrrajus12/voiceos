using System.Net;
using System.Text;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>A reference back to a window VoiceOS established resolves to that exact window, or clarifies.</summary>
public sealed class ReferentWindowTests
{
    private sealed class FakeHttpHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static readonly WindowCandidate Notes = new("w1", "notepad", "Untitled - Notepad", Hwnd: 11);
    private static readonly WindowCandidate Paint = new("w2", "mspaint", "Untitled - Paint", Hwnd: 12, IsForeground: true);
    private static readonly WindowCandidate Other = new("w3", "notepad", "todo - Notepad", Hwnd: 13);

    private static DecisionState State(string transcript, string[]? referents, params WindowCandidate[] windows)
        => new(transcript, "Paint", [], windows, [MediaOperation.Play], [SnapDirection.Left, SnapDirection.Right],
            ReferentWindowIds: referents);

    private static TypeSafeJevDecisionEngine Engine(string json)
        => new("key", new HttpClient(new FakeHttpHandler(json, HttpStatusCode.OK)), "jev-latest", 0.35, 0.40,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);

    private static string Answers(string mode, string? window, string action = "CloseCurrentWindow", string extra = "")
    {
        var target = window is null ? "" : $", \"target_window\": {{\"type\":\"choice\",\"choice\":\"{window}\",\"confidence\":0.95}}";
        return "{ \"answers\": { \"is_command\": {\"type\":\"noul\",\"noul\":0.96}, \"is_compound\": {\"type\":\"noul\",\"noul\":0.02}, "
            + $"\"action_kind\": {{\"type\":\"choice\",\"choice\":\"{action}\",\"confidence\":0.97}}, "
            + $"\"window_target_mode\": {{\"type\":\"choice\",\"choice\":\"{mode}\",\"confidence\":0.95}}{target}{extra} }}, \"usage\": {{}} }}";
    }

    [Fact]
    public void ReferentModeIsOfferedOnlyWhenAValidReferentWindowExists()
    {
        var engine = Engine("{}");
        var without = engine.BuildBaseRequest(State("close it", null, Notes, Paint)).Questions["window_target_mode"].Criteria!;
        Assert.False(without.ContainsKey("Referent"));
        Assert.Contains("'it'", without["Current"]);
        var with = engine.BuildBaseRequest(State("close it", ["w1"], Notes, Paint));
        var criteria = with.Questions["window_target_mode"].Criteria!;
        Assert.True(criteria.ContainsKey("Referent"));
        Assert.DoesNotContain("'it'", criteria["Current"]);
        Assert.Contains("recently opened or used by VoiceOS", with.Questions["target_window"].Criteria!["w1"]);
        Assert.DoesNotContain("recently", with.Questions["target_window"].Criteria!["w2"]);
    }

    [Fact]
    public async Task ItResolvesToTheReferentWindowNotTheForegroundOne()
    {
        var result = await Engine(Answers("Referent", "w1")).DecideAsync(State("close it", ["w1"], Notes, Paint));
        Assert.False(result.Plan.RequiresClarification);
        var step = Assert.IsType<CloseWindowStep>(SemanticProgramPlanner.ToSingleStepProgram(result.Plan)!.Steps[0]);
        Assert.Equal("w1", Assert.IsType<CandidateWindowTarget>(step.Target).WindowCandidateId);
        Assert.Null(result.Plan.WindowAppCandidateId);
    }

    [Fact]
    public async Task TwoWindowsOfTheSameApp_FollowUpTargetsTheExactHwndCandidate()
    {
        var json = Answers("Referent", "w3", "SnapCurrentWindow", ", \"snap_dir\": {\"type\":\"choice\",\"choice\":\"Left\",\"confidence\":0.95}");
        var result = await Engine(json).DecideAsync(State("snap that one left", ["w3", "w1"], Notes, Other, Paint));
        Assert.Equal("w3", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task PickOutsideTheReferentSet_Clarifies_NeverActs()
    {
        var result = await Engine(Answers("Referent", "w2")).DecideAsync(State("close it", ["w1", "w3"], Notes, Other, Paint));
        Assert.True(result.Plan.RequiresClarification);
        Assert.Null(result.Plan.WindowCandidateId);
    }

    [Fact]
    public async Task MissingPick_WithExactlyOneReferentWindow_UsesIt()
    {
        var result = await Engine(Answers("Referent", null)).DecideAsync(State("close it", ["w1"], Notes, Paint));
        Assert.Equal("w1", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task MissingPick_WithSeveralReferentWindows_Clarifies()
    {
        var result = await Engine(Answers("Referent", null)).DecideAsync(State("close it", ["w1", "w3"], Notes, Other, Paint));
        Assert.True(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task ReferentModeWithoutReferentWindows_IsTreatedAsCurrent()
    {
        var result = await Engine(Answers("Referent", "w1")).DecideAsync(State("close it", null, Notes, Paint));
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
    }

    [Fact]
    public async Task ThisStaysTheForegroundWindow_EvenWhenReferentsExist()
    {
        var result = await Engine(Answers("Current", "w1")).DecideAsync(State("close this", ["w1"], Notes, Paint));
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
    }
}

public sealed class NativeReferentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    private static readonly WindowCandidate Notes = new("result:s1", "notepad", "Untitled - Notepad", Hwnd: 11);
    private static BrowserTabInfo DocsTab => new(1, 1, true, "https://a.test/", "docs", BrowserTabProvenance.VoiceOs, "s");

    [Fact]
    public void ExactStepResultWindow_BecomesAnAppWindowReferent()
    {
        var program = new VoiceProgram([new OpenAppStep("s1", new AppTarget("notepad")),
            new SnapWindowStep("s2", new StepResultTarget("s1"), SnapDirection.Left)]);
        var result = new ProgramResult([VoiceStepResult.Ok("s1", Notes), VoiceStepResult.Ok("s2", Notes)]);
        var native = NativeReferents.FromProgram(program, result, Now);
        var referent = Assert.Single(native.Observed.DistinctBy(r => r.Key));
        Assert.Equal(ReferentKind.AppWindow, referent.Kind);
        Assert.Equal((nint)11, referent.Hwnd);
        Assert.Equal("notepad", referent.ProcessName);
        Assert.Equal(ReferentProvenance.DirectStepResult, referent.Provenance);
    }

    [Fact]
    public void FailedStepsAndWindowlessResults_StoreNothing()
    {
        var program = new VoiceProgram([new OpenAppStep("s1", new AppTarget("x")), new MediaControlStep("s2", MediaOperation.Play)]);
        var result = new ProgramResult([VoiceStepResult.ExecutionFailed("s1", "no"), VoiceStepResult.Ok("s2")]);
        var native = NativeReferents.FromProgram(program, result, Now);
        Assert.Empty(native.Observed);
        Assert.Empty(native.Closed);
    }

    [Fact]
    public void ClosedWindow_IsInvalidatedNotObserved()
    {
        var program = new VoiceProgram([new CloseWindowStep("s1", new CandidateWindowTarget("w1"))]);
        var native = NativeReferents.FromProgram(program, new ProgramResult([VoiceStepResult.Ok("s1", Notes with { Id = "w1" })]), Now);
        Assert.Empty(native.Observed);
        Assert.Equal([(nint)11], native.Closed);
    }

    [Fact]
    public void SameHwndContinuation_RefreshesTheSameReferent()
    {
        var store = new ReferentStore();
        var program = new VoiceProgram([new OpenAppStep("s1", new AppTarget("notepad"))]);
        for (var i = 0; i < 2; i++)
            store.ObserveMany(NativeReferents.FromProgram(program, new ProgramResult([VoiceStepResult.Ok("s1", Notes)]), Now).Observed);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ImplicitWindows_AreOnlyThoseNewerThanTheLatestBrowserReferent()
    {
        var store = new ReferentStore();
        store.Observe(new(ReferentKind.AppWindow, "old", ReferentProvenance.DirectStepResult, Now, Hwnd: 11, ProcessName: "notepad"));
        store.Observe(new(ReferentKind.Page, "docs", ReferentProvenance.NavigationResult, Now, 1, "s", "https://a.test/"));
        store.Observe(new(ReferentKind.AppWindow, "new", ReferentProvenance.DirectStepResult, Now, Hwnd: 12, ProcessName: "mspaint"));
        var windows = new[] { new WindowCandidate("w1", "notepad", "old", Hwnd: 11), new WindowCandidate("w2", "mspaint", "new", Hwnd: 12) };
        var valid = store.Validate(new(true, [DocsTab], windows), Now);
        Assert.Equal(["w2"], NativeReferents.ImplicitWindowIds(valid, windows));
    }

    [Fact]
    public void BrowserAfterNative_MeansImplicitItStaysWithTheBrowser()
    {
        var store = new ReferentStore();
        store.Observe(new(ReferentKind.AppWindow, "old", ReferentProvenance.DirectStepResult, Now, Hwnd: 11, ProcessName: "notepad"));
        store.Observe(new(ReferentKind.Page, "docs", ReferentProvenance.NavigationResult, Now, 1, "s", "https://a.test/"));
        var windows = new[] { new WindowCandidate("w1", "notepad", "old", Hwnd: 11) };
        var valid = store.Validate(new(true, [DocsTab], windows), Now);
        Assert.Empty(NativeReferents.ImplicitWindowIds(valid, windows));
    }
}
