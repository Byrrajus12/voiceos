using System.Net;
using System.Text;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// Tests for VoicePlan construction: action-specific confidence, code-owned completeness,
/// and speculative answer isolation. Per-task coverage from the M3 design review.
/// </summary>
public class VoicePlanConstructionTests
{
    private static DecisionState MakeState(string transcript = "open chrome")
        => new(
            transcript,
            "Notepad",
            [new AppCandidate("chrome", "Google Chrome", "chrome"), new AppCandidate("edge", "Microsoft Edge")],
            [new WindowCandidate("w0", "notepad", "Untitled - Notepad", true)],
            [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
            [SnapDirection.Left, SnapDirection.Right]);

    private static TypeSafeJevDecisionEngine MakeEngine(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(json, status);
        return new TypeSafeJevDecisionEngine("key", new HttpClient(handler), "jev-latest", 0.35, 0.40,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);
    }

    // Task 1: OpenApp confidence must ignore window/media/text speculative answers.
    [Fact]
    public async Task OpenApp_Confidence_IgnoresUnrelatedSpeculativeAnswers()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.96},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.99},
                "target_app": {"type":"choice","choice":"chrome","confidence":1.00},
                "target_window": {"type":"choice","choice":"w0","confidence":0.60},
                "media_op": {"type":"choice","choice":"Pause","confidence":0.55},
                "text": {"type":"choice","choice":"c0","confidence":0.50}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState());

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal("Google Chrome", result.Plan.AppCandidate);
        // confidence = min(action=0.99, app=1.00); window/media/text must not lower it
        Assert.InRange(result.Plan.Confidence, 0.98, 1.00);
    }

    // Task 1: FocusWindow confidence must ignore app/media/text speculative answers.
    [Fact]
    public async Task FocusWindow_Confidence_IgnoresUnrelatedSpeculativeAnswers()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.94},
                "action_kind": {"type":"choice","choice":"FocusWindow","confidence":0.96},
                "target_window": {"type":"choice","choice":"w0","confidence":0.97},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.40},
                "media_op": {"type":"choice","choice":"Next","confidence":0.55}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("switch to vscode"));

        Assert.Equal(VoiceAction.FocusWindow, result.Plan.Action);
        Assert.Equal("Untitled - Notepad", result.Plan.WindowCandidate);
        // confidence = min(action=0.96, window=0.97); app/media must not lower it
        Assert.InRange(result.Plan.Confidence, 0.95, 0.97);
    }

    // Task 1 & 3: MediaControl confidence uses media_op only, not target_app/window/text.
    // This is the "pause this" regression case.
    [Fact]
    public async Task MediaControl_Confidence_UsesMediaOpOnly_NotTargetApp()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.82},
                "action_kind": {"type":"choice","choice":"MediaControl","confidence":0.97},
                "media_op": {"type":"choice","choice":"Pause","confidence":1.00},
                "target_app": {"type":"choice","choice":"w0","confidence":0.62},
                "target_window": {"type":"choice","choice":"w0","confidence":0.50}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("pause this"));

        Assert.Equal(VoiceAction.MediaControl, result.Plan.Action);
        Assert.Equal(MediaOperation.Pause, result.Plan.Media);
        // confidence = min(action=0.97, media_op=1.00); target_app 0.62 must NOT lower it
        Assert.InRange(result.Plan.Confidence, 0.96, 1.00);
    }

    // Task 2 & 4: "pause this" — high MediaControl + Pause → complete plan, no clarification.
    [Fact]
    public async Task PauseThis_CompleteMediaPlan_NoClarificationRequired()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.82},
                "action_kind": {"type":"choice","choice":"MediaControl","confidence":0.97},
                "media_op": {"type":"choice","choice":"Pause","confidence":1.00},
                "target_app": {"type":"choice","choice":"w0","confidence":0.62}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("pause this"));

        Assert.Equal(VoiceAction.MediaControl, result.Plan.Action);
        Assert.Equal(MediaOperation.Pause, result.Plan.Media);
        Assert.False(result.Plan.RequiresClarification,
            "MediaControl with Pause op is complete; generic is_complete must not cause false clarification");
    }

    // Task 2: MaximizeCurrentWindow requires no extra target — must never require clarification.
    [Fact]
    public async Task MaximizeCurrentWindow_AlwaysComplete_NoClarificationRequired()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.91},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.30},
                "target_window": {"type":"choice","choice":"w0","confidence":0.20}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("maximize this"));

        Assert.Equal(VoiceAction.MaximizeCurrentWindow, result.Plan.Action);
        Assert.False(result.Plan.RequiresClarification);
        // Only action confidence is used; speculative answers must not lower it
        Assert.InRange(result.Plan.Confidence, 0.90, 0.92);
    }

    // Task 2: OpenApp without a resolved app target → RequiresClarification.
    [Fact]
    public async Task OpenApp_MissingAppTarget_RequiresClarification()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.85}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("open the other one"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification);
    }

    // Task 4: Speculative high-confidence unrelated answer cannot change plan action or confidence.
    [Fact]
    public async Task SpeculativeHighConfidenceUnrelatedAnswer_DoesNotAffectPlan()
    {
        // OpenApp action but a very high-confidence window answer — must be ignored
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.85},
                "target_window": {"type":"choice","choice":"w0","confidence":0.99},
                "media_op": {"type":"choice","choice":"Next","confidence":0.99}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("open chrome"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Null(result.Plan.WindowCandidate);
        Assert.Null(result.Plan.Media);
        // window=0.99 and media=0.99 must NOT lower confidence below min(action=0.90, app=0.85)
        Assert.InRange(result.Plan.Confidence, 0.84, 0.86);
    }

    // Task 5: Casual non-command → None.
    [Fact]
    public async Task CasualNonCommand_ReturnsNone()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.10},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.85}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("the weather is pretty nice today"));
        Assert.Equal(VoiceAction.None, result.Plan.Action);
    }

    // Regression: MediaControl + Next correctly classified.
    [Fact]
    public async Task MediaControl_NextOp_CorrectPlan()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"MediaControl","confidence":0.88},
                "media_op": {"type":"choice","choice":"Next","confidence":0.95}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("skip this"));

        Assert.Equal(VoiceAction.MediaControl, result.Plan.Action);
        Assert.Equal(MediaOperation.Next, result.Plan.Media);
        Assert.False(result.Plan.RequiresClarification);
        Assert.InRange(result.Plan.Confidence, 0.87, 0.89);
    }

    // Low action confidence → Rejected.
    [Fact]
    public async Task LowActionConfidence_ReturnsRejected()
    {
        var json = """
            {
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.30}
              }, "usage": {}
            }
            """;

        var result = await MakeEngine(json).DecideAsync(MakeState("go back"));
        Assert.Equal(VoiceAction.Rejected, result.Plan.Action);
    }

    // Task 8: Verify action_kind request criteria contain examples and contrastive descriptions.
    [Fact]
    public void ActionKind_Criteria_ContainExamplesAndContrastiveDescriptions()
    {
        var engine = MakeEngine("{}");
        var req = engine.BuildRequest(MakeState());

        Assert.True(req.Questions.TryGetValue("action_kind", out var q));
        Assert.NotNull(q.Criteria);

        // MediaControl criteria must include media-specific examples to handle "skip this", "next song"
        Assert.Contains("skip", q.Criteria["MediaControl"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pause", q.Criteria["MediaControl"], StringComparison.OrdinalIgnoreCase);

        // OpenApp should distinguish from FocusWindow via "may not be running"
        Assert.Contains("pull up", q.Criteria["OpenApp"], StringComparison.OrdinalIgnoreCase);

        // None should describe what it is, not just "no action"
        Assert.Contains("conversation", q.Criteria["None"], StringComparison.OrdinalIgnoreCase);

        // is_complete must not be in the request (removed)
        Assert.False(req.Questions.ContainsKey("is_complete"),
            "is_complete must be removed from the Jev request");
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        { _body = body; _status = status; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
