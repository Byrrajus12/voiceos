using System.Net;
using System.Text;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// F3 invariant: a forced semantic choice can never by itself become an executable app target.
/// target_app offers none; a named window operation keeps its resolved window unless the app's
/// identity corroborates it.
/// </summary>
public sealed class DirectTargetIdentityTests
{
    private static readonly AppCandidate ChromeApp = new("google-chrome", "Google Chrome", "chrome", "Chrome");
    private static readonly AppCandidate MoreApp = new("more", "More...");
    private static readonly AppCandidate PaintApp = new("paint", "Paint", "mspaint", "Microsoft.Paint_8wekyb3d8bbwe!App");

    private static DecisionState State(string transcript, params WindowCandidate[] windows)
        => new(transcript, "Notepad", [ChromeApp, MoreApp, PaintApp], windows,
            [MediaOperation.Play, MediaOperation.Pause], [SnapDirection.Left, SnapDirection.Right]);

    private static TypeSafeJevDecisionEngine Engine(string json)
        => new("key", new HttpClient(new FakeHttpHandler(json, HttpStatusCode.OK)), "jev-latest", 0.35, 0.40,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);

    private static string Answers(string body) => $$"""
        { "answers": { "is_command": {"type":"noul","noul":0.96}, "is_compound": {"type":"noul","noul":0.02}, {{body}} }, "usage": {} }
        """;

    [Fact]
    public void TargetAppOffersNoneAfterInstalledApps()
    {
        var engine = Engine(Answers(""" "action_kind": {"type":"choice","choice":"None","confidence":0.9} """));
        var baseChoices = engine.BuildBaseRequest(State("open calculator")).Questions["target_app"].Criteria!;
        var unitChoices = engine.BuildCompoundRequest(State("open calculator")).Questions["unit_1_target_app"].Criteria!;
        Assert.Equal("google-chrome", baseChoices.Keys.First());
        Assert.Equal(TypeSafeJevDecisionEngine.NoAppChoice, baseChoices.Keys.Last());
        Assert.Contains(TypeSafeJevDecisionEngine.NoAppChoice, unitChoices.Keys);
    }

    [Fact]
    public void NoInstalledAppsMeansNoTargetAppQuestion()
    {
        var engine = Engine(Answers(""" "action_kind": {"type":"choice","choice":"None","confidence":0.9} """));
        var state = new DecisionState("open calculator", "Notepad", [], [], [], []);
        Assert.False(engine.BuildBaseRequest(state).Questions.ContainsKey("target_app"));
    }

    [Fact]
    public async Task OpenAppResolvedToNoneProducesNoDirectProgram()
    {
        var json = Answers("""
            "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.97},
            "target_app": {"type":"choice","choice":"none","confidence":0.93}
            """);
        var result = await Engine(json).DecideAsync(State("open Final Cut Pro"));
        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification);
        Assert.Null(result.Plan.AppCandidateId);
        Assert.Null(result.Plan.AppCandidate);
        Assert.Null(SemanticProgramPlanner.ToSingleStepProgram(result.Plan));
    }

    [Fact]
    public async Task OpenAppWithUnofferedChoiceIsNotATarget()
    {
        var json = Answers("""
            "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.97},
            "target_app": {"type":"choice","choice":"final-cut-pro","confidence":0.93}
            """);
        var result = await Engine(json).DecideAsync(State("open Final Cut Pro"));
        Assert.True(result.Plan.RequiresClarification);
        Assert.Null(result.Plan.AppCandidate);
    }

    private static string NamedMaximize(string window, string app) => Answers($$"""
        "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.97},
        "window_target_mode": {"type":"choice","choice":"Named","confidence":0.95},
        "target_window": {"type":"choice","choice":"{{window}}","confidence":0.95},
        "target_app": {"type":"choice","choice":"{{app}}","confidence":0.80}
        """);

    [Fact]
    public async Task NamedWindowOperationKeepsResolvedWindowWhenAppDoesNotCorroborate()
    {
        // "Maximize Paint": the window resolved to Paint, the forced app choice picked an unrelated app.
        var paintWindow = new WindowCandidate("w1", "mspaint", "Untitled - Paint", Hwnd: 11,
            AppUserModelId: "Microsoft.Paint_8wekyb3d8bbwe!App");
        var result = await Engine(NamedMaximize("w1", "more")).DecideAsync(State("maximize Paint", paintWindow));
        Assert.Null(result.Plan.WindowAppCandidateId);
        var step = Assert.IsType<MaximizeWindowStep>(SemanticProgramPlanner.ToSingleStepProgram(result.Plan)!.Steps[0]);
        Assert.Equal("w1", Assert.IsType<CandidateWindowTarget>(step.Target).WindowCandidateId);
    }

    [Fact]
    public async Task NamedWindowOperationUsesAppWhenIdentityCorroboratesWindow()
    {
        // "Snap Chrome left": the Chrome app matches its window, keeping execution-time ambiguity checks.
        var chromeWindow = new WindowCandidate("w2", "chrome", "News - Google Chrome", Hwnd: 12, AppUserModelId: "Chrome");
        var result = await Engine(NamedMaximize("w2", "google-chrome")).DecideAsync(State("maximize Chrome", chromeWindow));
        Assert.Equal("google-chrome", result.Plan.WindowAppCandidateId);
        var step = Assert.IsType<MaximizeWindowStep>(SemanticProgramPlanner.ToSingleStepProgram(result.Plan)!.Steps[0]);
        Assert.Equal("google-chrome", Assert.IsType<AppTarget>(step.Target).AppCandidateId);
    }

    [Theory]
    // AUMIDs on both sides decide; a mismatch is never overridden by process name.
    [InlineData("chrome", "Chrome", "chrome", "Chrome", true)]
    [InlineData("chrome", "Chrome._crx_abc", "chrome", "Chrome", false)]
    [InlineData("chrome", null, "chrome", "Chrome", true)]
    [InlineData("mspaint", "Microsoft.Paint_8wekyb3d8bbwe!App", "mspaint", null, true)]
    [InlineData(null, null, "explorer", null, false)]
    [InlineData("Code", null, "chrome", null, false)]
    public void AppCorroboratesWindowByIdentity(string? appProcess, string? appAumid,
        string windowProcess, string? windowAumid, bool expected)
    {
        var app = new AppCandidate("a", "A", appProcess, appAumid);
        var window = new WindowCandidate("w", windowProcess, "T", AppUserModelId: windowAumid);
        Assert.Equal(expected, TypeSafeJevDecisionEngine.AppCorroboratesWindow(app, window));
    }

    // ── Compound units: a named unit without an app never falls back to the foreground ──

    private static JevAnswer Choice(string value) => new("choice", value, new Dictionary<string, double> { [value] = .9 }, .9);

    [Fact]
    public void CompoundNamedUnitResolvedToNoneIsNotTheForegroundWindow()
    {
        var planner = new SemanticProgramPlanner(0.35, 0.40);
        var answers = new Dictionary<string, JevAnswer>
        {
            ["is_command"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .95 }, .95),
            ["unit_count"] = Choice("2"),
            ["unit_1_action_kind"] = Choice("OpenApp"),
            ["unit_1_target_app"] = Choice("google-chrome"),
            ["unit_2_action_kind"] = Choice("MinimizeCurrentWindow"),
            ["unit_2_window_target_mode"] = Choice("Named"),
            ["unit_2_target_app"] = Choice(TypeSafeJevDecisionEngine.NoAppChoice)
        };
        var program = planner.TryBuildProgram(answers, State("open Chrome and minimize Final Cut"),
            new Dictionary<string, string>());
        Assert.NotNull(program);
        Assert.Single(program!.Steps);
        Assert.IsType<OpenAppStep>(program.Steps[0]);

        answers["unit_1_action_kind"] = Choice("MinimizeCurrentWindow");
        answers["unit_1_window_target_mode"] = Choice("Named");
        answers["unit_1_target_app"] = Choice(TypeSafeJevDecisionEngine.NoAppChoice);
        Assert.Null(planner.TryBuildProgram(answers, State("minimize Final Cut and open Chrome"),
            new Dictionary<string, string>()));
    }

    [Fact]
    public void CompoundOpenAppResolvedToNoneIsNotAnOpenStep()
    {
        var planner = new SemanticProgramPlanner(0.35, 0.40);
        var answers = new Dictionary<string, JevAnswer>
        {
            ["is_command"] = new("noul", "true", new Dictionary<string, double> { ["noul"] = .95 }, .95),
            ["unit_count"] = Choice("1"),
            ["unit_1_action_kind"] = Choice("OpenApp"),
            ["unit_1_target_app"] = Choice(TypeSafeJevDecisionEngine.NoAppChoice)
        };
        Assert.Null(planner.TryBuildProgram(answers, State("open Calculator"), new Dictionary<string, string>()));
    }

    private sealed class FakeHttpHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
