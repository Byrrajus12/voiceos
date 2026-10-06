using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

public sealed class JevAnswerParserTests
{
    private static JevAnswer Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JevAnswerParser.Parse(doc.RootElement);
    }

    [Fact]
    public void Choice_NestedProbabilities_AreParsed_ConfidenceUnchanged()
    {
        var a = Parse("""{"type":"choice","choice":"billing","probabilities":{"billing":0.88,"technical":0.12,"sales":0},"confidence":0.81}""");
        Assert.Equal(3, a.Probabilities.Count);
        Assert.Equal(.88, a.Probabilities["billing"]);
        Assert.Equal(.81, a.Confidence);
    }

    [Fact]
    public void Choice_TopRunnerUpMargin_AreCorrect()
    {
        var a = Parse("""{"choice":"a","probabilities":{"a":0.6,"b":0.3,"c":0.1}}""");
        Assert.True(a.HasDistribution);
        Assert.Equal(("a", .6), a.Top);
        Assert.Equal(("b", .3), a.RunnerUp);
        Assert.Equal(.3, a.Margin);
    }

    [Fact]
    public void Choice_AbsentProbabilities_NoDistribution()
    {
        var a = Parse("""{"choice":"a","confidence":0.8}""");
        Assert.False(a.HasDistribution);
        Assert.Null(a.Top); Assert.Null(a.RunnerUp); Assert.Null(a.Margin);
    }

    [Fact]
    public void Choice_NestedBeatsTopLevelNumericFields()
    {
        var a = Parse("""{"choice":"a","a":0.1,"b":0.9,"probabilities":{"a":0.8,"b":0.2}}""");
        Assert.Equal(.8, a.Probabilities["a"]);
        Assert.Equal(.2, a.Probabilities["b"]);
    }

    [Fact]
    public void Choice_TopLevelNumericFallback_StillParsed()
    {
        var a = Parse("""{"choice":"a","a":0.8,"b":0.2,"confidence":0.7}""");
        Assert.Equal(.8, a.Probabilities["a"]); Assert.Equal(.7, a.Confidence);
    }

    [Fact]
    public void Choice_Tie_PrefersSelectedChoice_ThenOrdinal()
    {
        var a = Parse("""{"choice":"c","probabilities":{"b":0.3,"c":0.3,"a":0.3}}""");
        Assert.Equal("c", a.Top?.Choice); Assert.Equal("a", a.RunnerUp?.Choice); Assert.Equal(0, a.Margin);
    }

    [Fact]
    public void Choice_SelectedNotArgMax_KeepsSelected_FlagsMismatch()
    {
        var a = Parse("""{"choice":"b","probabilities":{"a":0.8,"b":0.2}}""");
        Assert.Equal("b", a.SelectedChoice); Assert.False(a.SelectedIsArgMax);
    }

    [Theory]
    [InlineData(-0.1)] [InlineData(1.1)]
    public void Choice_NestedValueOutOfRange_Throws(double p)
        => Assert.Throws<InvalidOperationException>(() => Parse(JsonSerializer.Serialize(new { choice = "a", probabilities = new { a = p } })));

    [Fact]
    public void Choice_NonNumericNestedMember_Ignored()
        => Assert.Single(Parse("""{"choice":"a","probabilities":{"a":0.8,"b":"bad"}}""").Probabilities);

    [Fact]
    public void Noul_Unchanged()
    {
        var a = Parse("""{"type":"noul","noul":0.8}""");
        Assert.Equal(.8, a.Probabilities["noul"]); Assert.Equal(.8, a.Confidence);
        Assert.Equal(("true", .8), a.Top); Assert.Equal(.6, a.Margin!.Value, 10);
    }

    private sealed class Response(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private const string Envelope = """{"answers":{"route":{"choice":"DIRECT_CAPABILITY","confidence":0.8,"probabilities":{"DIRECT_CAPABILITY":0.9,"CLARIFY":0.1}},"is_command":{"type":"noul","noul":0.9},"action_kind":{"choice":"None","confidence":0.8,"probabilities":{"None":0.9,"OpenApp":0.1}}}}""";

    [Fact]
    public async Task Gateway_EndToEnd_NestedDistributionSurvives()
    {
        var gateway = new TypeSafeJevGateway("test", "test", new HttpClient(new Response(Envelope)), NullLogger<TypeSafeJevGateway>.Instance);
        var a = await gateway.AskAsync(new { }, new Dictionary<string, JevQuestionDto>());
        Assert.Equal(.9, a["route"].Top?.P);
    }

    [Fact]
    public async Task DirectEngine_EndToEnd_NestedDistributionSurvives()
    {
        var engine = new TypeSafeJevDecisionEngine("test", new HttpClient(new Response(Envelope)), "test", .35, .4, NullLogger<TypeSafeJevDecisionEngine>.Instance);
        var result = await engine.DecideAsync(new("test", "", [], [], [], []));
        Assert.Equal(.9, result.RawAnswers["action_kind"].Top?.P);
    }

    [Theory]
    [InlineData("DIRECT_CAPABILITY", .44)] [InlineData("DIRECT_CAPABILITY", .46)]
    [InlineData("COMPUTER_USE", .44)] [InlineData("COMPUTER_USE", .46)]
    [InlineData("CLARIFY", .44)] [InlineData("CLARIFY", .46)]
    public async Task ExistingThresholds_Unchanged(string choice, double confidence)
    {
        async Task<CommandRouteDecision> Route(bool nested)
        {
            var json = JsonSerializer.Serialize(new { answers = new { route = new { choice, confidence, probabilities = nested ? new Dictionary<string, double> { [choice] = .8, ["other"] = .2 } : null } } });
            var gateway = new TypeSafeJevGateway("test", "test", new HttpClient(new Response(json)), NullLogger<TypeSafeJevGateway>.Instance);
            return await new TypeSafeCommandRouter(gateway).RouteAsync("test");
        }
        var a = await Route(false); var b = await Route(true);
        Assert.Equal(a with { RawAnswers = null }, b with { RawAnswers = null });
    }
}
