using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace Fgvm.Godot.Download;

/// <summary>
///     Owns HTTP validation, timeouts, retries, and writing response bytes to disk.
/// </summary>
internal sealed class HttpRangeTransfer(
    HttpClient httpClient,
    Func<RangeHeaderValue?, HttpRequestMessage> createRequest,
    ParallelRangedDownloader.Options options
)
{
    private const int ReadBufferSize = checked((int)ByteSize.Mebibyte);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly RetryPolicy _retryPolicy = new(options);

    public Task<HttpResponseMessage> ProbeAsync(CancellationToken cancellationToken) =>
        RetryAsync(async () =>
        {
            var response = await SendRequestAsync(
                new RangeHeaderValue(0, options.ChunkSize - 1), null, cancellationToken);
            if (!IsTransient(response.StatusCode))
            {
                // The caller may reuse this response as the first chunk, including its unread body.
                return response;
            }

            response.Dispose();
            throw new TransientDownloadException($"Probe returned {(int)response.StatusCode}.");
        }, cancellationToken);

    public async Task DownloadRangeAsync(RangeChunk chunk,
        long totalBytes,
        RangeEntityValidator validator,
        SafeFileHandle handle,
        RangeDownloadProgress progress,
        CancellationToken cancellationToken
    )
    {
        var state = _retryPolicy.StartRange(chunk.Start);
        var responseFromProbe = chunk.Response;

        while (state.CanRetry)
        {
            var attemptBytes = 0L;
            var remaining = new RangeChunk(state.NextOffset, chunk.End, responseFromProbe);
            responseFromProbe = null;
            try
            {
                await DownloadRangeAttemptAsync(
                    remaining, totalBytes, validator, handle,
                    bytesWritten =>
                    {
                        attemptBytes += bytesWritten;
                        progress.ReportBytesWritten(bytesWritten);
                    },
                    cancellationToken);

                return;
            }
            catch (TransientDownloadException)
            {
                state = state.AfterFailure(attemptBytes);
                if (!state.CanRetry)
                {
                    throw;
                }

                await Task.Delay(_retryPolicy.DelayAfter(state), cancellationToken);
            }
        }

        throw new InvalidOperationException("Range recovery started without an available attempt.");
    }

    private async Task DownloadRangeAttemptAsync(RangeChunk range,
        long totalBytes,
        RangeEntityValidator validator,
        SafeFileHandle handle,
        Action<int> bytesWritten,
        CancellationToken cancellationToken
    )
    {
        using var response = range.Response ?? await SendRequestAsync(
            new RangeHeaderValue(range.Start, range.End), validator, cancellationToken);
        ValidateRangeResponse(response, range, totalBytes);
        await WriteResponseBodyAsync(
            response, handle, range.Start, range.Length, bytesWritten, cancellationToken);
    }

    public Task DownloadSequentialAsync(HttpResponseMessage? probeResponse,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        // A 200 probe already contains the full response. Unsafe 206 probes arrive here as null.
        var firstResponse = probeResponse;
        var totalBytes = probeResponse?.Content.Headers.ContentLength;
        var attemptBytes = 0L;

        return RetryAsync(async () =>
        {
            using var response = firstResponse ?? await SendRequestAsync(null, null, cancellationToken);
            firstResponse = null;

            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (IsTransient(response.StatusCode))
                {
                    throw new TransientDownloadException(
                        $"Sequential download returned {(int)response.StatusCode}.");
                }

                throw new IOException($"Expected 200 for sequential download, got {(int)response.StatusCode}.");
            }

            totalBytes ??= response.Content.Headers.ContentLength;
            attemptBytes = 0;
            using var handle = File.OpenHandle(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
            await WriteResponseBodyAsync(
                response, handle, 0, totalBytes,
                bytesWritten =>
                {
                    attemptBytes += bytesWritten;
                    progress?.Report(new DownloadProgress(attemptBytes, totalBytes));
                },
                cancellationToken);
        }, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(RangeHeaderValue? range,
        RangeEntityValidator? validator,
        CancellationToken cancellationToken
    )
    {
        using var request = createRequest(range);
        validator?.Apply(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.StallTimeout);
        try
        {
            // Return after the headers so body reads can enforce their own stall timeout.
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new TransientDownloadException("The server did not respond before the stall timeout.", ex);
        }
        catch (Exception ex) when (IsTransientTransportFailure(ex, cancellationToken))
        {
            throw new TransientDownloadException("The HTTP request failed.", ex);
        }
    }

    private static void ValidateRangeResponse(HttpResponseMessage response, RangeChunk chunk, long totalBytes)
    {
        // A 206 alone is not enough: an unexpected range would be written at the wrong file offset.
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (IsTransient(response.StatusCode))
            {
                throw new TransientDownloadException(
                    $"Range {chunk.Start}-{chunk.End} returned {(int)response.StatusCode}.");
            }

            throw new IOException(
                $"Expected 206 for range {chunk.Start}-{chunk.End}, got {(int)response.StatusCode}.");
        }

        var range = response.Content.Headers.ContentRange;
        if (range is not { From: { } from, To: { } to, Length: { } length } ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            from != chunk.Start || to != chunk.End || length != totalBytes)
        {
            throw new IOException(
                $"Expected Content-Range bytes {chunk.Start}-{chunk.End}/{totalBytes}, got {range?.ToString() ?? "(none)"}.");
        }
    }

    private async Task WriteResponseBodyAsync(HttpResponseMessage response,
        SafeFileHandle handle,
        long offset,
        long? expectedLength,
        Action<int>? bytesWritten,
        CancellationToken cancellationToken
    )
    {
        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex) when (IsTransientTransportFailure(ex, cancellationToken))
        {
            throw new TransientDownloadException("Failed to open the response body.", ex);
        }

        await using (stream)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                var position = offset;
                var written = 0L;
                while (expectedLength is not { } expected || written < expected)
                {
                    var bytesToRead = expectedLength is { } length
                        ? checked((int)Math.Min(buffer.Length, length - written))
                        : buffer.Length;
                    int bytesRead;
                    try
                    {
                        bytesRead = await DownloadStreamReader.ReadAsync(
                            stream, buffer.AsMemory(0, bytesToRead), options.StallTimeout, cancellationToken);
                    }
                    catch (Exception ex) when (IsTransientTransportFailure(ex, cancellationToken))
                    {
                        throw new TransientDownloadException("Failed while reading the response body.", ex);
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), position, cancellationToken);
                    position += bytesRead;
                    written += bytesRead;
                    bytesWritten?.Invoke(bytesRead);
                }

                if (expectedLength is { } declaredLength && written != declaredLength)
                {
                    throw new TransientDownloadException(
                        $"Expected {declaredLength} bytes starting at offset {offset}, but received {written}.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task RetryAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        await RetryAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);

    private async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _retryPolicy.MaxAttemptsWithoutProgress; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (TransientDownloadException) when (attempt < _retryPolicy.MaxAttemptsWithoutProgress)
            {
                await Task.Delay(_retryPolicy.DelayForAttempt(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("Retry started without an available attempt.");
    }

    // Retry statuses associated with temporary load or availability. Other 4xx responses
    // indicate a request or protocol problem that another attempt is unlikely to fix.
    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == (HttpStatusCode)429 ||
        (int)statusCode >= 500;

    // A fresh request can recover from transport and stream failures. Caller cancellation cannot.
    private static bool IsTransientTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException ||
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

    // Only this exception is caught by RetryAsync; validation failures deliberately bypass retries.
    private sealed class TransientDownloadException(string message, Exception? innerException = null)
        : IOException(message, innerException);

    private readonly record struct RangeRetryState(
        long NextOffset,
        int TotalAttemptsRemaining,
        int AttemptsWithoutProgressRemaining,
        int MaxAttemptsWithoutProgress
    )
    {
        public bool CanRetry => TotalAttemptsRemaining > 0 && AttemptsWithoutProgressRemaining > 0;

        public int BackoffAttempt => Math.Max(1, MaxAttemptsWithoutProgress - AttemptsWithoutProgressRemaining);

        public RangeRetryState AfterFailure(long bytesWritten) => new(
            checked(NextOffset + bytesWritten),
            checked(TotalAttemptsRemaining - 1),
            bytesWritten > 0
                ? MaxAttemptsWithoutProgress
                : checked(AttemptsWithoutProgressRemaining - 1),
            MaxAttemptsWithoutProgress);
    }

    private readonly record struct RetryPolicy(
        int MaxAttemptsWithoutProgress,
        int MaxTotalRangeAttempts,
        TimeSpan InitialDelay
    )
    {
        public RetryPolicy(ParallelRangedDownloader.Options options)
            : this(
                options.MaxAttemptsWithoutProgress,
                options.MaxTotalAttemptsPerRange,
                options.InitialRetryDelay)
        { }

        public RangeRetryState StartRange(long offset) => new(
            offset,
            MaxTotalRangeAttempts,
            MaxAttemptsWithoutProgress,
            MaxAttemptsWithoutProgress);

        public TimeSpan DelayAfter(RangeRetryState state) => DelayForAttempt(state.BackoffAttempt);

        public TimeSpan DelayForAttempt(int attempt) => TimeSpan.FromMilliseconds(Math.Min(
            InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
            MaximumRetryDelay.TotalMilliseconds));
    }
}
