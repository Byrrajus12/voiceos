using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceOS.Core.Decision;
using Xunit;

namespace VoiceOS.Core.Tests.Decision;

public sealed class JevGatewayRetryTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    [InlineData(503)]
    public async Task TransientStatus_RetriesExactlyOnce(int status)
    {
        var handler = new Responses((HttpStatusCode)status, HttpStatusCode.OK);
        var gateway = Gateway(handler);
        var answers = await gateway.AskAsync(new { goal = "test" }, new Dictionary<string, JevQuestionDto>());
        Assert.Empty(answers);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RepeatedTransientStatus_StopsAfterOneRetry()
    {
        var handler = new Responses((HttpStatusCode)529, (HttpStatusCode)529);
        await Assert.ThrowsAsync<HttpRequestException>(() => Gateway(handler).AskAsync(new { }, new Dictionary<string, JevQuestionDto>()));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task NonTransientStatus_DoesNotRetry()
    {
        var handler = new Responses(HttpStatusCode.BadRequest, HttpStatusCode.OK);
        await Assert.ThrowsAsync<HttpRequestException>(() => Gateway(handler).AskAsync(new { }, new Dictionary<string, JevQuestionDto>()));
        Assert.Equal(1, handler.Calls);
    }

    private static TypeSafeJevGateway Gateway(HttpMessageHandler handler)
        => new("placeholder", "test-model", new HttpClient(handler), NullLogger<TypeSafeJevGateway>.Instance);

    private sealed class Responses(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = statuses[Calls++];
            return Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("{\"answers\":{}}") });
        }
    }
}
