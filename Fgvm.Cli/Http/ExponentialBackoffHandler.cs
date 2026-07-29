using Fgvm.Extensions;

namespace Fgvm.Cli.Http;

internal sealed class ExponentialBackoffHandler : DelegatingHandler
{
    private static readonly TimeSpan MaximumRetryAfterDelay = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _initialDelay;
    private readonly int _maxRetries;

    public ExponentialBackoffHandler(TimeSpan initialDelay, int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetries);

        _initialDelay = initialDelay;
        _maxRetries = maxRetries;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The ranged downloader retries probes, chunks, and its sequential fallback itself; retrying here would
        // multiply both its attempt budget and its backoff delays.
        if (request.IsSelfRetrying)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var attempt = 0;
        var delay = _initialDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan? serverRequestedDelay = null;

            try
            {
                var response = await base.SendAsync(request, cancellationToken);

                if (!response.StatusCode.IsTransient || attempt >= _maxRetries)
                {
                    return response;
                }

                // Read before disposing: the server's own pacing beats a guess, and 429 and 503 usually send one.
                serverRequestedDelay = ReadRetryAfter(response);
                response.Dispose();
            }
            catch (Exception ex) when (ex.IsTransientTransportFailure(cancellationToken) && attempt < _maxRetries)
            { }

            attempt++;

            try
            {
                await Task.Delay(serverRequestedDelay ?? delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            delay = TimeSpan.FromTicks(delay.Ticks * 2);
        }
    }

    /// <summary>
    ///     Reads Retry-After as either a delay or an absolute date, ignoring values that have already elapsed. The
    ///     result is capped so a long server-side cooldown fails fast instead of hanging the command.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var requested = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => (TimeSpan?)null
        };

        return requested is { } value && value > TimeSpan.Zero
            ? value < MaximumRetryAfterDelay ? value : MaximumRetryAfterDelay
            : null;
    }
}
