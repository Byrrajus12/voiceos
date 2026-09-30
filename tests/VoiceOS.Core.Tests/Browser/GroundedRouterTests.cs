using System.Text.Json;
using VoiceOS.Core.Activation;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class GroundedRouterTests
{
    private sealed class CaptureGateway : IJevGateway
    {
        public string State = "";
        public IReadOnlyDictionary<string, JevQuestionDto> Questions = new Dictionary<string, JevQuestionDto>();
        public Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(object state,
            IReadOnlyDictionary<string, JevQuestionDto> questions, CancellationToken cancellationToken = default)
        {
            State = JsonSerializer.Serialize(state);
            Questions = questions;
            return Task.FromResult<IReadOnlyDictionary<string, JevAnswer>>(new Dictionary<string, JevAnswer>());
        }
    }

    [Fact]
    public async Task GroundedRouter_StateIncludesCompactContext_AndNoDom()
    {
        var gateway = new CaptureGateway();
        var context = new FrontDoorContext(new("Chrome", "private", ForegroundKind.Browser, 999), [],
            new(true, 4, new(7, "example.org", "Search results", true, false), []),
            new("Search", "example.org", true, 12, true));
        await new TypeSafeCommandRouter(gateway).RouteAsync("open the third result", null, context);
        using var state = JsonDocument.Parse(gateway.State);
        Assert.True(state.RootElement.TryGetProperty("context", out var compact));
        Assert.True(compact.TryGetProperty("foreground", out _));
        Assert.True(compact.TryGetProperty("browser", out _));
        Assert.True(compact.TryGetProperty("recent_task", out _));
        Assert.False(state.RootElement.TryGetProperty("recentTask", out _));
        Assert.DoesNotContain("visible_text", gateway.State);
        Assert.DoesNotContain("elements", gateway.State);
        Assert.DoesNotContain("apps", gateway.State);
        Assert.DoesNotContain("999", gateway.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierReferenceHead_IsAskedOnlyWhenReferentsExist_InTheSameRequest(bool referents)
    {
        var gateway = new CaptureGateway();
        await new TypeSafeCommandRouter(gateway).RouteAsync("open that again", null, null, default, referents);
        Assert.Equal(referents, gateway.Questions.ContainsKey("earlier_reference"));
        Assert.True(gateway.Questions.ContainsKey("task_relation"));
    }

    [Fact]
    public async Task GroundedRouter_NullContext_StateUnchanged()
    {
        var gateway = new CaptureGateway();
        var router = new TypeSafeCommandRouter(gateway);
        var recent = new RecentTaskFrame(1, "s", "https://example.org/", "Search",
            VoiceOS.Core.Interaction.InteractionCompletionState.Complete, DateTimeOffset.UnixEpoch);
        await router.RouteAsync("close it", recent);
        var original = gateway.State;
        var originalQuestions = JsonSerializer.Serialize(gateway.Questions);
        await router.RouteAsync("close it", recent, null);
        Assert.Equal(original, gateway.State);
        Assert.Equal(JsonSerializer.Serialize(new { utterance = "close it", recentTask = new {
            recent.SemanticGoal, recent.Completion, recent.LastUsed } }), gateway.State);
        Assert.Equal(originalQuestions, JsonSerializer.Serialize(gateway.Questions));
    }

    [Fact]
    public async Task GroundedRouter_HeadSetUnchanged_OnlyFourInstructionsChange()
    {
        var gateway = new CaptureGateway();
        var router = new TypeSafeCommandRouter(gateway);
        await router.RouteAsync("close it");
        var old = gateway.Questions;
        await router.RouteAsync("close it", null, new FrontDoorContext(null, [], new(false, 0, null, []), null));
        Assert.Equal(14, gateway.Questions.Count);
        Assert.Equal(old.Keys.Order(), gateway.Questions.Keys.Order());
        Assert.DoesNotContain("surface", gateway.Questions.Keys);
        Assert.DoesNotContain("app", gateway.Questions.Keys);
        Assert.DoesNotContain("tab", gateway.Questions.Keys);
        var changed = old.Keys.Where(k => JsonSerializer.Serialize(old[k]) != JsonSerializer.Serialize(gateway.Questions[k])).Order();
        Assert.Equal(new[] { "context_dependency", "route", "tab_disposition", "task_relation" }, changed);
    }
}
