using System.Net;
using System.Text;
using System.Text.Json;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using VoiceOS.Core.Execution;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// Tests the two-pass Jev request architecture:
///   BuildBaseRequest — single-action questions + is_compound routing signal, no unit_N_*.
///   BuildCompoundRequest — unit_count + unit_N_* questions only, no base questions.
///   DecideAsync routing — single command → base only; compound command → base + compound.
/// </summary>
public class JevRequestBuilderTests
{
    private static TypeSafeJevDecisionEngine MakeEngine(HttpMessageHandler? handler = null)
        => new("test-key",
               handler != null ? new HttpClient(handler) : new HttpClient(),
               "jev-latest", 0.35, 0.40,
               Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);

    private static DecisionState MakeState(string transcript = "open chrome")
        => new(
            transcript,
            "Notepad",
            [new AppCandidate("chrome", "Google Chrome", "chrome"), new AppCandidate("edge", "Microsoft Edge", "msedge")],
            [new WindowCandidate("w0", "notepad", "Untitled - Notepad", true)],
            [MediaOperation.Play, MediaOperation.Pause],
            [SnapDirection.Left, SnapDirection.Right]);

    // ── Base request structure ────────────────────────────────────────────────

    [Fact]
    public void BaseRequest_HasCorrectModel()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.Equal("jev-latest", req.Model);
    }

    [Fact]
    public void BaseRequest_StateHasUtterance()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState("launch firefox"));
        Assert.Equal("launch firefox", req.State.Utterance);
    }

    [Fact]
    public void BaseRequest_StateHasFrontmostApp()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.Equal("Notepad", req.State.FrontmostApp);
    }

    [Fact]
    public void BaseRequest_StateHasAppsList()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.Contains("Google Chrome", req.State.Apps);
        Assert.Contains("Microsoft Edge", req.State.Apps);
    }

    [Fact]
    public void BaseRequest_StateHasTextCandidates()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState("type hello world"));
        Assert.True(req.State.Candidates.ContainsKey("c0"));
        Assert.Equal("hello world", req.State.Candidates["c0"]);
    }

    [Fact]
    public void BaseRequest_HasIsCommandQuestion()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("is_command"));
        Assert.Equal("noul", req.Questions["is_command"].Type);
    }

    [Fact]
    public void BaseRequest_HasActionKindQuestion()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("action_kind"));
        Assert.Equal("choice", req.Questions["action_kind"].Type);
        Assert.NotNull(req.Questions["action_kind"].Criteria);
        Assert.True(req.Questions["action_kind"].Criteria!.ContainsKey("OpenApp"));
    }

    [Fact]
    public void BaseRequest_HasIsCompoundQuestion()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("is_compound"));
        Assert.Equal("noul", req.Questions["is_compound"].Type);
    }

    [Fact]
    public void BaseRequest_HasTargetAppQuestion_WhenAppsProvided()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("target_app"));
        Assert.Equal("chrome", req.Questions["target_app"].Criteria!.Keys.First());
    }

    [Fact]
    public void BaseRequest_HasTargetWindowQuestion_WhenWindowsProvided()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("target_window"));
        Assert.True(req.Questions["target_window"].Criteria!.ContainsKey("w0"));
    }

    [Fact]
    public void BaseRequest_NoTargetWindowQuestion_WhenNoWindows()
    {
        var state = new DecisionState("close this", "Notepad",
            [new AppCandidate("chrome", "Google Chrome")], [],
            [], []);
        var req = MakeEngine().BuildBaseRequest(state);
        Assert.False(req.Questions.ContainsKey("target_window"));
    }

    [Fact]
    public void BaseRequest_HasMediaOpQuestion()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("media_op"));
        Assert.True(req.Questions["media_op"].Criteria!.ContainsKey("Play"));
        Assert.True(req.Questions["media_op"].Criteria!.ContainsKey("Next"));
    }

    [Fact]
    public void BaseRequest_HasSnapDirQuestion()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("snap_dir"));
        Assert.True(req.Questions["snap_dir"].Criteria!.ContainsKey("Left"));
        Assert.True(req.Questions["snap_dir"].Criteria!.ContainsKey("Right"));
    }

    [Fact]
    public void BaseRequest_DoesNotContainUnitQuestions()
    {
        // Base request must NEVER contain unit_N_* questions — those belong in the compound request
        var req = MakeEngine().BuildBaseRequest(MakeState("open chrome and minimize discord"));
        var unitKeys = req.Questions.Keys.Where(k => k.StartsWith("unit_")).ToList();
        Assert.Empty(unitKeys);
    }

    [Fact]
    public void BaseRequest_DoesNotContainUnitCount()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        Assert.False(req.Questions.ContainsKey("unit_count"));
    }

    [Fact]
    public void BaseRequest_SerializesToValidJson()
    {
        var req = MakeEngine().BuildBaseRequest(MakeState());
        var json = JsonSerializer.Serialize(req);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    // ── Compound request structure ────────────────────────────────────────────

    [Fact]
    public void CompoundRequest_HasUnitCountQuestion()
    {
        var req = MakeEngine().BuildCompoundRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("unit_count"));
        Assert.Equal("choice", req.Questions["unit_count"].Type);
    }

    [Fact]
    public void CompoundRequest_HasUnitActionKindForAllUnits()
    {
        var req = MakeEngine().BuildCompoundRequest(MakeState());
        for (int i = 1; i <= 4; i++)
            Assert.True(req.Questions.ContainsKey($"unit_{i}_action_kind"),
                $"Missing unit_{i}_action_kind");
    }

    [Fact]
    public void CompoundRequest_HasPriorRefForUnits2to4()
    {
        var req = MakeEngine().BuildCompoundRequest(MakeState());
        Assert.False(req.Questions.ContainsKey("unit_1_prior_ref"));  // unit 1 has no prior
        Assert.True(req.Questions.ContainsKey("unit_2_prior_ref"));
        Assert.True(req.Questions.ContainsKey("unit_3_prior_ref"));
        Assert.True(req.Questions.ContainsKey("unit_4_prior_ref"));
    }

    [Fact]
    public void CompoundRequest_HasTargetAppPerUnit_WhenAppsProvided()
    {
        var req = MakeEngine().BuildCompoundRequest(MakeState());
        for (int i = 1; i <= 4; i++)
            Assert.True(req.Questions.ContainsKey($"unit_{i}_target_app"),
                $"Missing unit_{i}_target_app");
    }

    [Fact]
    public void CompoundRequest_DoesNotContainBaseQuestions()
    {
        // Compound request should not repeat is_command, action_kind, etc.
        var req = MakeEngine().BuildCompoundRequest(MakeState());
        Assert.False(req.Questions.ContainsKey("is_command"));
        Assert.False(req.Questions.ContainsKey("action_kind"));
        Assert.False(req.Questions.ContainsKey("is_compound"));
    }

    // ── Two-pass routing via DecideAsync ─────────────────────────────────────

    [Fact]
    public async Task SingleCommand_OnlyBaseRequestInvoked()
    {
        // When is_compound = false in base response, compound request must NOT be sent
        var tracker = new RequestTracker();
        // Base response: is_compound false → single command
        tracker.EnqueueResponse(BuildJevResponse(new Dictionary<string, object>
        {
            ["is_command"] = new { type = "noul", noul = 0.95, confidence = 0.95 },
            ["action_kind"] = new { type = "choice", choice = "OpenApp", confidence = 0.9 },
            ["is_compound"] = new { type = "noul", noul = 0.05, confidence = 0.95 },
            ["target_app"] = new { type = "choice", choice = "chrome", confidence = 0.9 },
            ["activation_mode"] = new { type = "choice", choice = "FocusOrLaunch", confidence = 0.9 },
        }));

        var engine = MakeEngine(tracker);
        var state = MakeState("open chrome");
        await engine.DecideAsync(state);

        Assert.Equal(1, tracker.RequestCount);
    }

    [Fact]
    public async Task CompoundCommand_BaseAndCompoundRequestInvoked()
    {
        // When is_compound = true in base response, compound request MUST be sent
        var tracker = new RequestTracker();
        // Base response: is_compound true
        tracker.EnqueueResponse(BuildJevResponse(new Dictionary<string, object>
        {
            ["is_command"] = new { type = "noul", noul = 0.95, confidence = 0.95 },
            ["action_kind"] = new { type = "choice", choice = "OpenApp", confidence = 0.9 },
            ["is_compound"] = new { type = "noul", noul = 0.92, confidence = 0.92 },
            ["target_app"] = new { type = "choice", choice = "chrome", confidence = 0.9 },
            ["activation_mode"] = new { type = "choice", choice = "FocusOrLaunch", confidence = 0.9 },
        }));
        // Compound response
        tracker.EnqueueResponse(BuildJevResponse(new Dictionary<string, object>
        {
            ["unit_count"] = new { type = "choice", choice = "2", confidence = 0.9 },
            ["unit_1_action_kind"] = new { type = "choice", choice = "OpenApp", confidence = 0.9 },
            ["unit_1_target_app"] = new { type = "choice", choice = "chrome", confidence = 0.9 },
            ["unit_1_activation_mode"] = new { type = "choice", choice = "FocusOrLaunch", confidence = 0.9 },
            ["unit_2_action_kind"] = new { type = "choice", choice = "MinimizeCurrentWindow", confidence = 0.9 },
            ["unit_2_window_target_mode"] = new { type = "choice", choice = "Named", confidence = 0.9 },
            ["unit_2_target_app"] = new { type = "choice", choice = "edge", confidence = 0.9 },
            ["unit_2_prior_ref"] = new { type = "noul", noul = 0.05, confidence = 0.95 },
        }));

        var engine = MakeEngine(tracker);
        var state = MakeState("open chrome and minimize edge");
        await engine.DecideAsync(state);

        Assert.Equal(2, tracker.RequestCount);
    }

    [Fact]
    public async Task SingleCommand_DecideAsync_ReturnsPlanWithCorrectAction()
    {
        // For single commands (is_compound=false), no compound request is sent.
        // DecisionResult.Program is null; Plan carries the action.
        // The orchestrator converts Plan → VoiceProgram via ToSingleStepProgram.
        var tracker = new RequestTracker();
        tracker.EnqueueResponse(BuildJevResponse(new Dictionary<string, object>
        {
            ["is_command"] = new { type = "noul", noul = 0.95, confidence = 0.95 },
            ["action_kind"] = new { type = "choice", choice = "AdjustVolume", confidence = 0.9 },
            ["is_compound"] = new { type = "noul", noul = 0.04, confidence = 0.96 },
            ["volume_direction"] = new { type = "choice", choice = "Down", confidence = 0.9 },
        }));

        var engine = MakeEngine(tracker);
        var state = MakeState("turn the volume down");
        var result = await engine.DecideAsync(state);

        Assert.Equal(1, tracker.RequestCount);
        // Single command: Program is null (no unit_N answers in base request)
        Assert.Null(result.Program);
        // Plan carries the decision — orchestrator uses ToSingleStepProgram(Plan) as fallback
        Assert.Equal(VoiceAction.AdjustVolume, result.Plan.Action);
        Assert.Equal(VolumeDirection.Down, result.Plan.VolumeAdjust);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildJevResponse(Dictionary<string, object> answers)
    {
        var body = new
        {
            answers,
            usage = new { input_tokens = 100, output_tokens = 50 }
        };
        return JsonSerializer.Serialize(body);
    }

    private sealed class RequestTracker : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new();
        public int RequestCount { get; private set; }

        public void EnqueueResponse(string json)
            => _responses.Enqueue(json);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = _responses.Count > 0
                ? _responses.Dequeue()
                : BuildJevResponse([]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
