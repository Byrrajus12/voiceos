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
            ["descriptor"] = "the official documentation", ["unresolvedReference"] = false,
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
    public async Task ValidDescriptorAndFlag_AreParsed_AndOldFieldsAreUnchanged()
    {
        var (result, _) = await Normalize(Payload());
        Assert.NotNull(result);
        Assert.Equal("the official documentation", result.Descriptor);
        Assert.False(result.UnresolvedReference);
        Assert.Equal("open the docs", result.Objective);
        Assert.Equal("documentation page", result.ResourceType);
        Assert.Equal(["docs"], result.SearchQueries);
        Assert.Equal("the docs page is open", result.CompletionHint);
        Assert.Equal(SemanticEndState.ResourceOpened, result.EndState);
    }

    [Fact]
    public async Task UnresolvedReferenceTrue_IsParsed()
    {
        var (result, _) = await Normalize(Payload(p => p["unresolvedReference"] = true));
        Assert.True(result!.UnresolvedReference);
    }

    [Fact]
    public async Task NullDescriptor_IsNull()
    {
        var (result, _) = await Normalize(Payload(p => p["descriptor"] = null));
        Assert.NotNull(result);
        Assert.Null(result.Descriptor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("bad\u0007control")]
    public async Task UnusableDescriptor_IsDropped_WithoutRejectingTheGoal(string descriptor)
    {
        var (result, _) = await Normalize(Payload(p => p["descriptor"] = descriptor));
        Assert.NotNull(result);
        Assert.Null(result.Descriptor);
        Assert.Equal(SemanticEndState.ResourceOpened, result.EndState);
    }

    [Fact]
    public async Task OversizedDescriptor_IsDropped_AndTheBoundaryIsAccepted()
    {
        var (over, _) = await Normalize(Payload(p => p["descriptor"] = new string('a', 121)));
        Assert.NotNull(over);
        Assert.Null(over.Descriptor);
        var (edge, _) = await Normalize(Payload(p => p["descriptor"] = new string('a', 120)));
        Assert.Equal(120, edge!.Descriptor!.Length);
    }

    [Fact]
    public async Task MissingNewFields_FallBackToSafeDefaults()
    {
        var (result, _) = await Normalize(Payload(p => { p.Remove("descriptor"); p.Remove("unresolvedReference"); }));
        Assert.NotNull(result);
        Assert.Null(result.Descriptor);
        Assert.False(result.UnresolvedReference);
    }

    [Fact]
    public async Task WrongTypedNewFields_FallBackToSafeDefaults()
    {
        var (result, _) = await Normalize(Payload(p => { p["descriptor"] = 5; p["unresolvedReference"] = "yes"; }));
        Assert.NotNull(result);
        Assert.Null(result.Descriptor);
        Assert.False(result.UnresolvedReference);
    }

    [Fact]
    public async Task ExistingRejections_StillReject()
    {
        Assert.Null((await Normalize(Payload(p => p["endState"] = "Unspecified"))).Result);
        Assert.Null((await Normalize(Payload(p => p["objective"] = new string('x', 241)))).Result);
    }

    [Fact]
    public async Task Schema_IsStrictAndRequiresTheNewFields_AndDoesNotAskForFamilyOrOperations()
    {
        var (_, request) = await Normalize(Payload());
        var schema = request.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        var body = schema.GetProperty("schema");
        Assert.False(body.GetProperty("additionalProperties").GetBoolean());
        var properties = body.GetProperty("properties").EnumerateObject().Select(static p => p.Name).ToHashSet();
        var required = body.GetProperty("required").EnumerateArray().Select(static e => e.GetString()!).ToHashSet();

        Assert.Equal(properties, required); // strict mode: every property is required
        Assert.Contains("descriptor", properties);
        Assert.Contains("unresolvedReference", properties);
        Assert.Equal("boolean", body.GetProperty("properties").GetProperty("unresolvedReference").GetProperty("type").GetString());
        foreach (var forbidden in new[] { "family", "proofFamily", "operation", "elementId", "ordinal", "plan", "steps", "proof" })
            Assert.DoesNotContain(forbidden, properties);
        foreach (var old in new[] { "objective", "entity", "resourceType", "preferredService", "preferredServiceUrl",
                     "searchQueries", "completionHint", "endState", "correctedTerms" })
            Assert.Contains(old, properties);
    }

    [Fact]
    public void Prompt_DefinesTheNewFields_WithoutAskingForProofFamily()
    {
        Assert.Contains("descriptor", OpenRouterBrowserGoalNormalizer.Prompt);
        Assert.Contains("unresolvedReference", OpenRouterBrowserGoalNormalizer.Prompt);
        Assert.DoesNotContain("proof", OpenRouterBrowserGoalNormalizer.Prompt, StringComparison.OrdinalIgnoreCase);
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
