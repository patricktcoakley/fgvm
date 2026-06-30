namespace Fgvm.Services;

/// <summary>
///     Reads streamed download content with a per-read stall timeout.
/// </summary>
internal static class DownloadStreamReader
{
    /// <summary>
    ///     Reads the next download chunk, failing when no bytes arrive before the stall timeout expires.
    /// </summary>
    /// <param name="stream">The response body stream to read from.</param>
    /// <param name="buffer">The destination buffer for the received bytes.</param>
    /// <param name="stallTimeout">The maximum time to wait for a single read to produce bytes.</param>
    /// <param name="cancellationToken">Cancellation token for caller-requested cancellation.</param>
    /// <returns>The number of bytes read, or zero when the stream reaches EOF.</returns>
    /// <exception cref="IOException">Thrown when the stream stalls for longer than <paramref name="stallTimeout" />.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is canceled.</exception>
    public static async Task<int> ReadAsync(Stream stream,
        byte[] buffer,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(stallTimeout);

        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new IOException(
                $"Download stalled for more than {FormatTimeout(stallTimeout)} without receiving data.",
                ex);
        }
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 1
            ? $"{timeout.TotalSeconds:0} seconds"
            : $"{timeout.TotalMilliseconds:0} milliseconds";
}
