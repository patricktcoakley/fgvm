using System.Net;
using Fgvm.Cli.Http;

namespace Fgvm.Tests.Http;

public sealed class ExponentialBackoffHandlerTests
{
    [Fact]
    public async Task SendAsync_RetriesTransientStatusCodes()
    {
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage((HttpStatusCode)429),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);

        using var response = await client.GetAsync("https://example.test/archive.zip", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_RetriesHttpRequestException()
    {
        var inner = new SequenceHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);

        using var response = await client.GetAsync("https://example.test/archive.zip", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryNonTransientStatusCode()
    {
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);

        using var response = await client.GetAsync("https://example.test/archive.zip", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_DoesNotSwallowCallerCancellation()
    {
        var inner = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync("https://example.test/archive.zip", cancellation.Token));

        Assert.Equal(0, inner.RequestCount);
    }

    private static HttpClient CreateClient(SequenceHandler inner)
    {
        var handler = new ExponentialBackoffHandler(TimeSpan.Zero, 3)
        {
            InnerHandler = inner
        };

        return new HttpClient(handler);
    }

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var responseIndex = Math.Min(RequestCount, responses.Length - 1);
            RequestCount++;
            var response = responses[responseIndex](request);
            return Task.FromResult(response);
        }
    }
}
