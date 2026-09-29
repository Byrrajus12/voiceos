using System.Net;
using System.Text.Json;
using VoiceOS.Core.Browser;
using VoiceOS.Core.Interaction;
using Xunit;

namespace VoiceOS.Core.Tests.Browser;

public sealed class BrowserGoalNormalizerTests
{
    private static Dictionary<string, object?> Payload(Action<Dictionary<string, object?>>? edit = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["objective"] = "open the docs", ["entity"] = null, ["resourceType"] = "documentation page",
            ["preferredService"] = null, ["preferredServiceUrl"] = null, ["searchQueries"] = new[] { "docs" },
            ["completionHint"] = "the docs page is open", ["endState"] = "ResourceOpened",
            ["correctedTerms"] = Array.Empty<object>()
        };
        edit?.Invoke(payload);
        return payload;
    }

    private static async Task<(BrowserGoalNormalization? Result, JsonDocument Request)> Normalize(Dictionary<string, object?> payload)
    {
        var handler = new Handler(JsonSerializer.Serialize(payload));
        var result = await new OpenRouterBrowserGoalNormalizer(new HttpClient(handler), "placeholder").NormalizeAsync("open the docs");
        return (result, JsonDocument.Parse(handler.Body!));
    }

    [Fact]
    public async Task ValidPayload_IsParsed()
    {
        var (result, _) = await Normalize(Payload());
        Assert.Equal("open the docs", result!.Objective);
        Assert.Equal(SemanticEndState.ResourceOpened, result.EndState);
        Assert.Equal(["docs"], result.SearchQueries);
    }

    [Fact]
    public async Task ExistingRejections_StillReject()
    {
        Assert.Null((await Normalize(Payload(p => p["endState"] = "Unspecified"))).Result);
        Assert.Null((await Normalize(Payload(p => p["objective"] = new string('x', 241)))).Result);
    }

    [Fact]
    public async Task Schema_IsStrict_AndAsksOnlyForTheOriginalFields()
    {
        var (_, request) = await Normalize(Payload());
        var schema = request.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        var body = schema.GetProperty("schema");
        var properties = body.GetProperty("properties").EnumerateObject().Select(static p => p.Name).ToHashSet();
        var required = body.GetProperty("required").EnumerateArray().Select(static e => e.GetString()!).ToHashSet();
        Assert.Equal(properties, required);
        Assert.Equal(new[] { "objective", "entity", "resourceType", "preferredService", "preferredServiceUrl",
            "searchQueries", "completionHint", "endState", "correctedTerms" }.ToHashSet(), properties);
    }

    [Fact]
    public async Task Request_KeepsTheOriginalTokenCap()
    {
        var (_, request) = await Normalize(Payload());
        Assert.Equal(300, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("low", request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    private sealed class Handler(string content) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            var envelope = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope) };
        }
    }
}
