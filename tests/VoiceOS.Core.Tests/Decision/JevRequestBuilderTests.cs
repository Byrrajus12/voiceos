using System.Text.Json;
using VoiceOS.Core.Candidates;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

public class JevRequestBuilderTests
{
    private static TypeSafeJevDecisionEngine MakeEngine()
        => new("test-key", new HttpClient(), "jev-latest", 0.35, 0.40,
               Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeSafeJevDecisionEngine>.Instance);

    private static DecisionState MakeState(string transcript = "open chrome")
        => new(
            transcript,
            "Notepad",
            [new AppCandidate("chrome", "Google Chrome", "chrome"), new AppCandidate("edge", "Microsoft Edge", "msedge")],
            [new WindowCandidate("w0", "notepad", "Untitled - Notepad", true)],
            [MediaOperation.Play, MediaOperation.Pause],
            [SnapDirection.Left, SnapDirection.Right]);

    [Fact]
    public void Request_HasCorrectModel()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.Equal("jev-latest", req.Model);
    }

    [Fact]
    public void Request_StateHasUtterance()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState("launch firefox"));
        Assert.Equal("launch firefox", req.State.Utterance);
    }

    [Fact]
    public void Request_StateHasFrontmostApp()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.Equal("Notepad", req.State.FrontmostApp);
    }

    [Fact]
    public void Request_StateHasAppsList()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.Contains("Google Chrome", req.State.Apps);
        Assert.Contains("Microsoft Edge", req.State.Apps);
    }

    [Fact]
    public void Request_StateHasTextCandidates()
    {
        // State.Candidates holds transcript-extracted text candidates (c0, c1...).
        // App/window IDs go in question criteria, not here.
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState("type hello world"));
        Assert.True(req.State.Candidates.ContainsKey("c0"));
        Assert.Equal("hello world", req.State.Candidates["c0"]);
    }

    [Fact]
    public void Request_HasIsCommandQuestion()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("is_command"));
        Assert.Equal("noul", req.Questions["is_command"].Type);
    }

    [Fact]
    public void Request_HasActionKindQuestion()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("action_kind"));
        Assert.Equal("choice", req.Questions["action_kind"].Type);
        Assert.NotNull(req.Questions["action_kind"].Criteria);
        Assert.True(req.Questions["action_kind"].Criteria!.ContainsKey("OpenApp"));
    }

    [Fact]
    public void Request_HasTargetAppQuestion_WhenAppsProvided()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("target_app"));
        Assert.Equal("chrome", req.Questions["target_app"].Criteria!.Keys.First());
    }

    [Fact]
    public void Request_HasTargetWindowQuestion_WhenWindowsProvided()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("target_window"));
        Assert.True(req.Questions["target_window"].Criteria!.ContainsKey("w0"));
    }

    [Fact]
    public void Request_NoTargetWindowQuestion_WhenNoWindows()
    {
        var engine = MakeEngine();
        var state = new DecisionState("close this", "Notepad",
            [new AppCandidate("chrome", "Google Chrome")], [],
            [], []);
        var req = engine.BuildRequest(state);
        Assert.False(req.Questions.ContainsKey("target_window"));
    }

    [Fact]
    public void Request_HasMediaOpQuestion()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("media_op"));
        Assert.True(req.Questions["media_op"].Criteria!.ContainsKey("Play"));
        Assert.True(req.Questions["media_op"].Criteria!.ContainsKey("Next"));
    }

    [Fact]
    public void Request_HasSnapDirQuestion()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        Assert.True(req.Questions.ContainsKey("snap_dir"));
        Assert.True(req.Questions["snap_dir"].Criteria!.ContainsKey("Left"));
        Assert.True(req.Questions["snap_dir"].Criteria!.ContainsKey("Right"));
    }

    [Fact]
    public void Request_SerializesToValidJson()
    {
        var engine = MakeEngine();
        var req = engine.BuildRequest(MakeState());
        var json = JsonSerializer.Serialize(req);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }
}
