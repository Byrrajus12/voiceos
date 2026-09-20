using System.Net;
using System.Text;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// Tests VoicePlan construction from synthetic Jev response JSON by driving
/// TypeSafeJevDecisionEngine through a fake HttpMessageHandler.
/// </summary>
public class JevResponseParserTests
{
    private static DecisionState MakeState(string transcript = "open chrome")
        => new(
            transcript,
            "Notepad",
            [new AppCandidate("chrome", "Google Chrome", "chrome"), new AppCandidate("edge", "Microsoft Edge")],
            [new WindowCandidate("w0", "notepad", "Untitled - Notepad", true)],
            [MediaOperation.Play, MediaOperation.Pause, MediaOperation.Toggle, MediaOperation.Next, MediaOperation.Previous],
            [SnapDirection.Left, SnapDirection.Right]);

    private static TypeSafeJevDecisionEngine MakeEngineWithResponse(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(responseJson, status);
        var http = new HttpClient(handler);
        return new TypeSafeJevDecisionEngine("test-key", http, "jev-latest", 0.35, 0.40,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);
    }

    [Fact]
    public async Task HighConfidenceOpenApp_ReturnsOpenAppPlan()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.90,"None":0.10,"confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","chrome":0.88,"edge":0.12,"confidence":0.88},
                "is_complete": {"type":"noul","noul":0.92}
              },
              "usage": {"input_tokens":150,"output_tokens":50}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState());

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal("Google Chrome", result.Plan.AppCandidate);
        Assert.False(result.Plan.RequiresClarification);
        Assert.InRange(result.Plan.Confidence, 0.87, 0.89);
        Assert.Equal(150, result.InputTokens);
        Assert.Equal(50, result.OutputTokens);
    }

    [Fact]
    public async Task LowIsCommandConfidence_ReturnsNonePlan()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.20},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("the weather is nice today"));

        Assert.Equal(VoiceAction.None, result.Plan.Action);
        Assert.NotEmpty(result.Plan.RejectionReason!);
    }

    [Fact]
    public async Task IsCommandFalse_ReturnsNonePlan()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.10}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("I need to think about this"));

        Assert.Equal(VoiceAction.None, result.Plan.Action);
    }

    [Fact]
    public async Task LowActionConfidence_ReturnsRejectedPlan()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.30,"None":0.70,"confidence":0.30},
                "is_complete": {"type":"noul","noul":0.80}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("go back"));

        Assert.Equal(VoiceAction.Rejected, result.Plan.Action);
    }

    [Fact]
    public async Task OpenApp_WithAppTarget_IsComplete_NotRequiresClarification()
    {
        // OpenApp completeness is code-owned: present app target → complete
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85},
                "target_app": {"type":"choice","choice":"chrome","chrome":0.70,"confidence":0.70}
              },
              "usage": {"input_tokens":120,"output_tokens":40}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("open chrome"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task OpenApp_MissingAppTarget_RequiresClarification()
    {
        // OpenApp completeness is code-owned: no app answer → RequiresClarification
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":120,"output_tokens":40}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("the other one"));

        Assert.True(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task MediaControl_ParsesMediaOp()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"MediaControl","MediaControl":0.92,"confidence":0.92},
                "media_op": {"type":"choice","choice":"Next","Next":0.88,"Pause":0.12,"confidence":0.88},
                "is_complete": {"type":"noul","noul":0.90}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("next song"));

        Assert.Equal(VoiceAction.MediaControl, result.Plan.Action);
        Assert.Equal(MediaOperation.Next, result.Plan.Media);
    }

    [Fact]
    public async Task SnapWindow_ParsesSnapDirection()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","SnapCurrentWindow":0.89,"confidence":0.89},
                "snap_dir": {"type":"choice","choice":"Right","Right":0.91,"Left":0.09,"confidence":0.91},
                "is_complete": {"type":"noul","noul":0.88}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap this right"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.Equal(SnapDirection.Right, result.Plan.Snap);
    }

    [Fact]
    public async Task HttpError_ReturnsRejectedPlan()
    {
        var engine = MakeEngineWithResponse("{}", HttpStatusCode.InternalServerError);
        var result = await engine.DecideAsync(MakeState());

        Assert.Equal(VoiceAction.Rejected, result.Plan.Action);
        Assert.NotNull(result.Plan.RejectionReason);
    }

    [Fact]
    public async Task MalformedJson_ReturnsRejectedPlan()
    {
        var engine = MakeEngineWithResponse("this is not json");
        var result = await engine.DecideAsync(MakeState());

        Assert.Equal(VoiceAction.Rejected, result.Plan.Action);
    }

    [Fact]
    public async Task EmptyAnswers_ReturnsNonePlan()
    {
        var json = """{"model":"jev-latest","answers":{},"usage":{"input_tokens":50,"output_tokens":10}}""";
        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState());

        Assert.Equal(VoiceAction.None, result.Plan.Action);
    }

    [Fact]
    public async Task RawAnswers_ArePopulated()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85},
                "is_complete": {"type":"noul","noul":0.88}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState());

        Assert.True(result.RawAnswers.ContainsKey("is_command"));
        Assert.True(result.RawAnswers.ContainsKey("action_kind"));
        Assert.Equal("noul", result.RawAnswers["is_command"].QuestionType);
        Assert.Equal("true", result.RawAnswers["is_command"].SelectedChoice);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHttpHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
