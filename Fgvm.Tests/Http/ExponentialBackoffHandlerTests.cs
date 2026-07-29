using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Fgvm.Cli.Http;
using Fgvm.Extensions;

namespace Fgvm.Tests.Http;

public sealed class ExponentialBackoffHandlerTests
{
    [Fact]
    public async Task SendAsync_RetriesTransientStatusCodes()
    {
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
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
    public async Task SendAsync_RetriesTransportFailuresSharedWithTheRangeDownloader()
    {
        // The retry decision is shared with HttpRangeTransfer, so a stream failure recovers here too.
        var inner = new SequenceHandler(
            _ => throw new IOException("the response stream ended early"),
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
    public async Task SendAsync_DoesNotRetrySelfRetryingRequests()
    {
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/archive.zip");
        request.MarkSelfRetrying();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_SelfRetryingRequestWithoutRangeHeader_IsStillSentOnce()
    {
        // The sequential fallback retries itself but carries no Range header, so the marker is the only signal.
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/archive.zip");
        request.MarkSelfRetrying();

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Null(request.Headers.Range);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_UnmarkedRangeRequest_StillRetries()
    {
        // Only the caller's own declaration suppresses retries; a bare Range header no longer does.
        var inner = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/archive.zip");
        request.Headers.Range = new RangeHeaderValue(0, 1023);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_HonorsRetryAfterDelayOverItsOwnBackoff()
    {
        var inner = new SequenceHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(250));
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        // A 10s ladder would dominate if Retry-After were ignored.
        using var client = CreateClient(inner, TimeSpan.FromSeconds(10));

        var elapsed = Stopwatch.StartNew();
        using var response = await client.GetAsync("https://example.test/archive.zip", CancellationToken.None);
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"waited {elapsed.Elapsed} instead of the requested 250ms");
    }

    [Fact]
    public async Task SendAsync_IgnoresRetryAfterDateAlreadyElapsed()
    {
        var inner = new SequenceHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5));
                return response;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = CreateClient(inner);

        using var response = await client.GetAsync("https://example.test/archive.zip", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.RequestCount);
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

    private static HttpClient CreateClient(SequenceHandler inner, TimeSpan? initialDelay = null)
    {
        var handler = new ExponentialBackoffHandler(initialDelay ?? TimeSpan.Zero, 3)
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
