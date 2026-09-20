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

    // Noul threshold tests — verifying the configured threshold is the sole gate
    [Fact]
    public async Task NoulAboveThresholdButBelowHalf_PassesCommandGate()
    {
        // Regression: noul=0.48 with threshold=0.35 must pass (was incorrectly rejected by hard 0.5 cutoff)
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.48},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","CloseCurrentWindow":0.80,"confidence":0.80}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close this"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
    }

    [Fact]
    public async Task NoulBelowThreshold_RejectsCommandGate()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.30},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","CloseCurrentWindow":0.90,"confidence":0.90}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("just thinking"));

        Assert.Equal(VoiceAction.None, result.Plan.Action);
    }

    [Fact]
    public async Task NoulAtExactThreshold_PassesCommandGate()
    {
        // noul exactly equal to threshold (0.35) must pass (>= semantics)
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.35},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","CloseCurrentWindow":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close this"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
    }

    [Fact]
    public async Task LiveStyleSkipThis_MediaControlNext()
    {
        // Mirrors the observed live failure: is_command=0.48, action_kind=MediaControl 0.96, media_op=Next 1.00
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.48},
                "action_kind": {"type":"choice","choice":"MediaControl","MediaControl":0.96,"confidence":0.96},
                "media_op": {"type":"choice","choice":"Next","Next":1.0,"confidence":1.0}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("skip this"));

        Assert.Equal(VoiceAction.MediaControl, result.Plan.Action);
        Assert.Equal(MediaOperation.Next, result.Plan.Media);
    }

    // ── SetVolume: numeric extraction from transcript ─────────────────────────

    [Theory]
    [InlineData("set volume to 50", 50)]
    [InlineData("set volume to zero", 0)]
    [InlineData("volume fifteen", 15)]
    [InlineData("set volume to one hundred", 100)]
    public async Task SetVolume_ExtractsVolumeValueFromTranscript(string transcript, int expectedPercent)
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"SetVolume","SetVolume":0.92,"confidence":0.92}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState(transcript));

        Assert.Equal(VoiceAction.SetVolume, result.Plan.Action);
        Assert.Equal(expectedPercent, result.Plan.VolumeValue);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task SetVolume_NoNumberInTranscript_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.90},
                "action_kind": {"type":"choice","choice":"SetVolume","SetVolume":0.85,"confidence":0.85}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("set volume"));

        Assert.Equal(VoiceAction.SetVolume, result.Plan.Action);
        Assert.Null(result.Plan.VolumeValue);
        Assert.True(result.Plan.RequiresClarification);
    }

    // ── AdjustVolume: direction extraction ────────────────────────────────────

    [Theory]
    [InlineData("turn it up", "Up", VolumeDirection.Up)]
    [InlineData("turn the volume down", "Down", VolumeDirection.Down)]
    [InlineData("make it louder", "Up", VolumeDirection.Up)]
    [InlineData("lower the volume", "Down", VolumeDirection.Down)]
    public async Task AdjustVolume_JevDirectionAnswer_ResolvesDirection(string transcript, string dirChoice, VolumeDirection expectedDir)
    {
        var json = $$"""
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"AdjustVolume","AdjustVolume":0.88,"confidence":0.88},
                "volume_direction": {"type":"choice","choice":"{{dirChoice}}","confidence":0.95}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState(transcript));

        Assert.Equal(VoiceAction.AdjustVolume, result.Plan.Action);
        Assert.Equal(expectedDir, result.Plan.VolumeAdjust);
        Assert.False(result.Plan.RequiresClarification);
    }

    // ── Named window target for Close/Snap ────────────────────────────────────

    [Fact]
    public async Task CloseCurrentWindow_WithHighConfidenceTargetWindow_PopulatesWindowCandidateId()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.90},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.92},
                "target_window": {"type":"choice","choice":"w0","confidence":0.85}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close Chrome"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Equal("w0", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task CloseCurrentWindow_LowConfidenceTargetWindow_NullCandidateId_UseForeground()
    {
        // "close this" → mode=Current, speculative low-confidence window — foreground, not clarification
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.90},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.89},
                "target_window": {"type":"choice","choice":"w0","confidence":0.20}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close this"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId); // foreground — no named target
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task SnapCurrentWindow_WithNamedTarget_PopulatesWindowCandidateId()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.91},
                "snap_dir": {"type":"choice","choice":"Left","confidence":0.95},
                "target_window": {"type":"choice","choice":"w0","confidence":0.82}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap Chrome left"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Equal(SnapDirection.Left, result.Plan.Snap);
        Assert.Equal("w0", result.Plan.WindowCandidateId);
    }

    // ── Activation mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenApp_ActivationModeFocusOrLaunch_DefaultAndExplicit()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.88},
                "activation_mode": {"type":"choice","choice":"FocusOrLaunch","confidence":0.92}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("open chrome"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal(AppActivationMode.FocusOrLaunch, result.Plan.ActivationMode);
    }

    [Fact]
    public async Task OpenApp_ActivationModeNewInstance_Parsed()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.88},
                "activation_mode": {"type":"choice","choice":"NewInstance","confidence":0.91}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("open a new Chrome window"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal(AppActivationMode.NewInstance, result.Plan.ActivationMode);
    }

    [Fact]
    public async Task OpenApp_NoActivationModeAnswer_DefaultsFocusOrLaunch()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.88}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("open chrome"));

        Assert.Equal(AppActivationMode.FocusOrLaunch, result.Plan.ActivationMode);
    }

    // ── FocusWindow: ambiguous target → RequiresClarification ─────────────────

    [Fact]
    public async Task FocusWindow_LowTargetWindowConfidence_RequiresClarification()
    {
        // Window confidence below action threshold — ambiguous multiple windows case
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"FocusWindow","confidence":0.88},
                "target_window": {"type":"choice","choice":"w0","confidence":0.25}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("switch to Chrome"));

        Assert.Equal(VoiceAction.FocusWindow, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification,
            "Low target_window confidence must trigger clarification to preserve honest ambiguity");
    }

    // ── AppProcessName threading ───────────────────────────────────────────────

    [Fact]
    public async Task OpenApp_AppProcessName_ThreadedFromCatalog()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.95},
                "action_kind": {"type":"choice","choice":"OpenApp","confidence":0.90},
                "target_app": {"type":"choice","choice":"chrome","confidence":0.88}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        // MakeState has AppCandidate chrome with ProcessName="chrome"
        var result = await engine.DecideAsync(MakeState("open chrome"));

        Assert.Equal(VoiceAction.OpenApp, result.Plan.Action);
        Assert.Equal("chrome", result.Plan.AppProcessName);
    }

    // ── Regression: Jev request contains activation_mode question ─────────────

    [Fact]
    public void Request_HasActivationModeQuestion()
    {
        var engine = MakeEngineWithResponse("{}");
        var state = MakeState();
        var req = engine.BuildRequest(state);

        Assert.True(req.Questions.ContainsKey("activation_mode"));
        Assert.True(req.Questions["activation_mode"].Criteria!.ContainsKey("FocusOrLaunch"));
        Assert.True(req.Questions["activation_mode"].Criteria!.ContainsKey("NewInstance"));
    }

    // ── Regression: CloseCurrentWindow description discourages OpenApp routing ─

    [Fact]
    public void ActionKind_CloseCurrentWindow_DescriptionMentionsNamedTargets()
    {
        var engine = MakeEngineWithResponse("{}");
        var req = engine.BuildRequest(MakeState());

        var closeDesc = req.Questions["action_kind"].Criteria!["CloseCurrentWindow"];
        // Must mention named-app examples so Jev prefers this over OpenApp for "close Chrome"
        Assert.Contains("Chrome", closeDesc, StringComparison.OrdinalIgnoreCase);
        // Must mention that intent is closing, not launching
        Assert.Contains("dismissal", closeDesc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActionKind_OpenApp_DescriptionExcludesCloseIntent()
    {
        var engine = MakeEngineWithResponse("{}");
        var req = engine.BuildRequest(MakeState());

        var openDesc = req.Questions["action_kind"].Criteria!["OpenApp"];
        // Must explicitly say not to use for close/quit
        Assert.Contains("not", openDesc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("close", openDesc, StringComparison.OrdinalIgnoreCase);
    }

    // ── Window target mode: Current ───────────────────────────────────────────

    [Fact]
    public async Task CloseThis_TargetModeCurrent_NullWindowCandidateId_NotRequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.91}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close this"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task MaximizeThis_TargetModeCurrent_NullWindowCandidateId_NotRequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.91},
                "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.87},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.90}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("maximize this"));

        Assert.Equal(VoiceAction.MaximizeCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task SnapThisLeft_TargetModeCurrent_NullWindowCandidateId_ValidDirection()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","confidence":0.90},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.92},
                "snap_dir": {"type":"choice","choice":"Left","confidence":0.94}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap this left"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.Equal(SnapDirection.Left, result.Plan.Snap);
        Assert.False(result.Plan.RequiresClarification);
    }

    // ── Window target mode: Named ─────────────────────────────────────────────

    [Fact]
    public async Task CloseChrome_TargetModeNamed_HighConfidence_WindowCandidateIdPopulated()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.94},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.91},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.93},
                "target_window": {"type":"choice","choice":"w0","confidence":0.87}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close Chrome"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Equal("w0", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task MaximizeVsCode_TargetModeNamed_HighConfidence_WindowCandidateIdPopulated()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.89},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.91},
                "target_window": {"type":"choice","choice":"w0","confidence":0.84}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("maximize VS Code"));

        Assert.Equal(VoiceAction.MaximizeCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Equal("w0", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task MinimizeChrome_TargetModeNamed_HighConfidence_WindowCandidateIdPopulated()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"MinimizeCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.90},
                "target_window": {"type":"choice","choice":"w0","confidence":0.85}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("minimize Chrome"));

        Assert.Equal(VoiceAction.MinimizeCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Equal("w0", result.Plan.WindowCandidateId);
        Assert.False(result.Plan.RequiresClarification);
    }

    // ── Window target mode: Named + low-confidence → RequiresClarification ────

    [Fact]
    public async Task CloseChrome_TargetModeNamed_LowConfidenceWindow_RequiresClarification_NeverForeground()
    {
        // THE SAFETY INVARIANT: Named intent with unresolvable target must never become "close foreground".
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.90},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.88},
                "target_window": {"type":"choice","choice":"w0","confidence":0.20}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close Chrome"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId); // not resolved — low confidence
        Assert.True(result.Plan.RequiresClarification,
            "Named target with low confidence must RequiresClarification — must never silently become 'close foreground'");
    }

    [Fact]
    public async Task MaximizeChrome_TargetModeNamed_LowConfidenceWindow_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.87},
                "target_window": {"type":"choice","choice":"w0","confidence":0.18}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("maximize Chrome"));

        Assert.Equal(VoiceAction.MaximizeCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.True(result.Plan.RequiresClarification,
            "Named target with low confidence must RequiresClarification");
    }

    [Fact]
    public async Task SnapChromeLeft_TargetModeNamed_LowConfidenceWindow_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","confidence":0.89},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.90},
                "snap_dir": {"type":"choice","choice":"Left","confidence":0.93},
                "target_window": {"type":"choice","choice":"w0","confidence":0.22}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap Chrome left"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Named, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.Equal(SnapDirection.Left, result.Plan.Snap);
        Assert.True(result.Plan.RequiresClarification,
            "Named target with low confidence must RequiresClarification — must not snap foreground instead");
    }

    // ── Speculative target noise: Current mode ignores high-confidence window ──

    [Fact]
    public async Task CloseThis_TargetModeCurrent_HighConfidenceSpeculativeWindow_CandidateDiscarded()
    {
        // "close this" → mode=Current even if Jev speculatively returns a high-confidence target_window.
        // Invariant: Current mode must carry null WindowCandidateId — speculative answer discarded.
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.90},
                "target_window": {"type":"choice","choice":"w0","confidence":0.80}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close this"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Equal(WindowTargetMode.Current, result.Plan.WindowTargetMode);
        Assert.Null(result.Plan.WindowCandidateId); // Current mode: speculative window always discarded
        Assert.False(result.Plan.RequiresClarification);
    }

    // ── Low-confidence target mode → RequiresClarification ───────────────────

    [Fact]
    public async Task CloseWindow_LowConfidenceTargetMode_RequiresClarification_NoExecution()
    {
        // Low-confidence window_target_mode is semantically ambiguous —
        // must not default to foreground or Named; must RequiresClarification.
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.88},
                "window_target_mode": {"type":"choice","choice":"Current","confidence":0.25},
                "target_window": {"type":"choice","choice":"w0","confidence":0.82}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Null(result.Plan.WindowCandidateId); // discarded — uncertain mode
        Assert.True(result.Plan.RequiresClarification,
            "Low-confidence window_target_mode is ambiguous — must RequiresClarification, not execute");
    }

    [Fact]
    public async Task SnapWindow_LowConfidenceTargetMode_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","confidence":0.89},
                "window_target_mode": {"type":"choice","choice":"Named","confidence":0.20},
                "snap_dir": {"type":"choice","choice":"Left","confidence":0.94},
                "target_window": {"type":"choice","choice":"w0","confidence":0.85}
              },
              "usage": {"input_tokens":100,"output_tokens":30}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap left"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification,
            "Low-confidence window_target_mode must RequiresClarification even when snap direction is known");
    }

    // ── Missing target mode → RequiresClarification ───────────────────────────

    [Fact]
    public async Task CloseWindow_MissingTargetModeAnswer_RequiresClarification()
    {
        // No window_target_mode in response — semantics unknown.
        // Must never silently assume Current (foreground) — must RequiresClarification.
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.92},
                "action_kind": {"type":"choice","choice":"CloseCurrentWindow","confidence":0.88}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("close window"));

        Assert.Equal(VoiceAction.CloseCurrentWindow, result.Plan.Action);
        Assert.Null(result.Plan.WindowCandidateId);
        Assert.True(result.Plan.RequiresClarification,
            "Missing window_target_mode for a window action must RequiresClarification — no safe default");
    }

    [Fact]
    public async Task MaximizeWindow_MissingTargetModeAnswer_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.91},
                "action_kind": {"type":"choice","choice":"MaximizeCurrentWindow","confidence":0.87}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("make it bigger"));

        Assert.Equal(VoiceAction.MaximizeCurrentWindow, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification);
    }

    [Fact]
    public async Task SnapWindow_MissingTargetModeAnswer_RequiresClarification()
    {
        var json = """
            {
              "model": "jev-latest",
              "answers": {
                "is_command": {"type":"noul","noul":0.93},
                "action_kind": {"type":"choice","choice":"SnapCurrentWindow","confidence":0.89},
                "snap_dir": {"type":"choice","choice":"Left","confidence":0.95}
              },
              "usage": {"input_tokens":80,"output_tokens":20}
            }
            """;

        var engine = MakeEngineWithResponse(json);
        var result = await engine.DecideAsync(MakeState("snap left"));

        Assert.Equal(VoiceAction.SnapCurrentWindow, result.Plan.Action);
        Assert.True(result.Plan.RequiresClarification,
            "Missing window_target_mode must RequiresClarification even when snap direction is known");
    }

    // ── window_target_mode question is in the Jev request ────────────────────

    [Fact]
    public void Request_HasWindowTargetModeQuestion()
    {
        var engine = MakeEngineWithResponse("{}");
        var req = engine.BuildRequest(MakeState());

        Assert.True(req.Questions.ContainsKey("window_target_mode"));
        Assert.True(req.Questions["window_target_mode"].Criteria!.ContainsKey("Current"));
        Assert.True(req.Questions["window_target_mode"].Criteria!.ContainsKey("Named"));
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
