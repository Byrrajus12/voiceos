using System.Net;
using System.Text;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

/// <summary>
/// Focused tests for noul answer deserialization and command-gating behavior.
/// </summary>
public class JevNoulDeserializationTests
{
    private static TypeSafeJevDecisionEngine MakeEngine(string json, double cmdThreshold = 0.35, double actionThreshold = 0.40)
    {
        var handler = new FakeHttpHandler(json);
        var http = new HttpClient(handler);
        return new TypeSafeJevDecisionEngine("test-key", http, "jev-latest", cmdThreshold, actionThreshold,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);
    }

    private static DecisionState OpenChromeState() => new(
        "Open chrome",
        "Notepad",
        [new AppCandidate("chrome", "Google Chrome", "chrome")],
        [],
        [],
        []);

    // ─── noul parsing ────────────────────────────────────────────────────────

    [Fact]
    public async Task NoulAnswer_ReadsNoulField_NotTrueOrFalseProperties()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        var isCmdAnswer = result.RawAnswers["is_command"];
        Assert.Equal("noul", isCmdAnswer.QuestionType);
        Assert.Equal(0.92, isCmdAnswer.Probabilities["noul"], precision: 9);
        Assert.Equal("true", isCmdAnswer.SelectedChoice);  // 0.92 >= 0.5
        Assert.Equal(0.92, isCmdAnswer.Confidence, precision: 9);
    }

    [Fact]
    public async Task NoulAnswer_LowProbability_SelectedChoiceIsFalse()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.18}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        var isCmdAnswer = result.RawAnswers["is_command"];
        Assert.Equal("false", isCmdAnswer.SelectedChoice);  // 0.18 < 0.5
        Assert.Equal(0.18, isCmdAnswer.Confidence, precision: 9);
    }

    [Fact]
    public async Task NoulAnswer_ExplicitConfidenceField_UsedOverNoulValue()
    {
        // If the API ever returns an explicit confidence alongside noul, it takes precedence.
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.88,"confidence":0.91}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        var isCmdAnswer = result.RawAnswers["is_command"];
        Assert.Equal(0.91, isCmdAnswer.Confidence, precision: 9);
    }

    [Fact]
    public async Task NoulAnswer_MissingNoulField_DefaultsToZeroProbability()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul"}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        var isCmdAnswer = result.RawAnswers["is_command"];
        Assert.Equal(0.0, isCmdAnswer.Probabilities["noul"]);
        Assert.Equal("false", isCmdAnswer.SelectedChoice);
        Assert.Equal(0.0, isCmdAnswer.Confidence);
    }

    // ─── command gating ──────────────────────────────────────────────────────

    [Fact]
    public async Task HighNoul_OpenChrome_PassesGateAndProducesOpenAppPlan()
    {
        // Regression test for the live failure: "open chrome" → None (0%)
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.90,"confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","chrome":0.88,"confidence":0.88},
                "is_complete": {"type":"noul","noul":0.95}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal("Google Chrome", result.Plan.AppCandidate);
        Assert.False(result.Plan.RequiresClarification);
        Assert.InRange(result.Plan.Confidence, 0.87, 0.89);
    }

    [Fact]
    public async Task NoulBelowThreshold_RejectsAsNotCommand()
    {
        // noul=0.30 is above 0.5 threshold for selectedChoice but below cmdThreshold=0.35
        // Actually 0.30 < 0.5 so selectedChoice="false" → rejected
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.30},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        Assert.Equal(VoiceAction.None, result.Plan.Action);
        Assert.NotEmpty(result.Plan.RejectionReason!);
    }

    [Fact]
    public async Task NoulAbove50ButBelowCmdThreshold_RejectedByConfidenceGate()
    {
        // noul=0.52: selectedChoice="true" (≥0.5) but confidence=0.52 < cmdThreshold=0.60
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.52},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json, cmdThreshold: 0.60);
        var result = await engine.DecideAsync(OpenChromeState());

        Assert.Equal(VoiceAction.None, result.Plan.Action);
    }

    [Fact]
    public async Task IsCompleteNoul_LowValue_SetsRequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85},
                "is_complete": {"type":"noul","noul":0.15}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        Assert.True(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task OpenApp_WithAppTarget_IsComplete()
    {
        // Completeness for OpenApp is now code-owned: a present target_app answer → complete.
        // is_complete is no longer asked or used.
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"OpenApp","OpenApp":0.85,"confidence":0.85},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.88}
              },
              "usage": {"input_tokens":50,"output_tokens":10}
            }
            """;

        var engine = MakeEngine(json);
        var result = await engine.DecideAsync(OpenChromeState());

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.False(result.Plan.RequiresClarification);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly string _body;

        public FakeHttpHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
