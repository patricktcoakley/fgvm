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
    private const int ReadBufferSize = 1024 * 1024;

    public async Task<HttpResponseMessage> ProbeAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage? result = null;
        await RetryAsync(async () =>
        {
            var response = await SendRequestAsync(
                new RangeHeaderValue(0, options.ChunkSize - 1), null, cancellationToken);
            if (!IsTransient(response.StatusCode))
            {
                // The caller may reuse this response as the first chunk, including its unread body.
                result = response;
                return;
            }

            response.Dispose();
            throw new TransientDownloadException($"Probe returned {(int)response.StatusCode}.");
        }, cancellationToken);
        return result!;
    }

    public Task DownloadRangeAsync(RangeChunk chunk,
        long totalBytes,
        RangeEntityValidator validator,
        SafeFileHandle handle,
        RangeDownloadProgress progress,
        CancellationToken cancellationToken
    )
    {
        var firstResponse = chunk.Response;
        return RetryAsync(async () =>
        {
            var written = 0L;
            try
            {
                using var response = firstResponse ?? await SendRequestAsync(
                    new RangeHeaderValue(chunk.Start, chunk.End), validator, cancellationToken);
                firstResponse = null;
                ValidateRangeResponse(response, chunk, totalBytes);
                await WriteResponseBodyAsync(
                    response, handle, chunk.Start, chunk.Length,
                    bytesWritten =>
                    {
                        written += bytesWritten;
                        progress.Add(bytesWritten);
                    },
                    cancellationToken);
            }
            catch
            {
                // Each retry starts the range over, so discard progress from the failed attempt.
                progress.Subtract(written);
                throw;
            }
        }, cancellationToken);
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
                while (true)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await DownloadStreamReader.ReadAsync(
                            stream, buffer, options.StallTimeout, cancellationToken);
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

                if (expectedLength is { } expected && written != expected)
                {
                    throw new TransientDownloadException(
                        $"Expected {expected} bytes starting at offset {offset}, but received {written}.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task RetryAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (TransientDownloadException) when (attempt < options.MaxAttempts)
            {
                // Backoff is bounded so a persistently unhealthy server cannot create very long pauses.
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
                    30_000));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
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
}
