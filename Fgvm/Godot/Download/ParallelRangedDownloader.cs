using System.Net;
using System.Net.Http.Headers;
using Fgvm.Types;

namespace Fgvm.Godot.Download;

/// <summary>
///     Reports download progress independent of any specific presentation format.
/// </summary>
/// <param name="BytesDownloaded">Total bytes written so far.</param>
/// <param name="TotalBytes">The full download size, or null when unknown.</param>
public readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes);

/// <summary>
///     Chooses between a parallel range download and a sequential fallback.
/// </summary>
internal static class ParallelRangedDownloader
{
    internal static async Task<Result<Unit, NetworkError>> DownloadAsync(HttpClient httpClient,
        string sourceUrl,
        Func<RangeHeaderValue?, HttpRequestMessage> createRequest,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        Options? options = null
    )
    {
        options ??= new Options();
        ValidateOptions(options);
        var transfer = new HttpRangeTransfer(httpClient, createRequest, options);

        try
        {
            // The destination is written directly, so never leave an older file in place.
            File.Delete(destinationPath);
            using var probeResponse = await transfer.ProbeAsync(cancellationToken);

            switch (probeResponse.StatusCode)
            {
                case HttpStatusCode.OK:
                    // The server ignored Range and returned the complete body; keep reading it.
                    await transfer.DownloadSequentialAsync(probeResponse, destinationPath, progress, cancellationToken);
                    return new Result<Unit, NetworkError>.Success(Unit.Value);

                case HttpStatusCode.PartialContent
                    when RangeDownloadPlan.TryCreate(probeResponse, options.ChunkSize, out var plan):
                    await DownloadRangesAsync(transfer, plan, destinationPath, progress, options, cancellationToken);
                    return new Result<Unit, NetworkError>.Success(Unit.Value);

                case HttpStatusCode.PartialContent:
                    // Parallel requests need a stable validator and a complete Content-Range.
                    // Start a normal request when the probe cannot guarantee both.
                    probeResponse.Dispose();
                    await transfer.DownloadSequentialAsync(null, destinationPath, progress, cancellationToken);
                    return new Result<Unit, NetworkError>.Success(Unit.Value);

                default:
                    var responseBody = await probeResponse.Content.ReadAsStringAsync(cancellationToken);
                    return new Result<Unit, NetworkError>.Failure(
                        new NetworkError.RequestFailure(sourceUrl, (int)probeResponse.StatusCode, responseBody));
            }
        }
        catch (OperationCanceledException cancellationError) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                File.Delete(destinationPath);
                throw;
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new OperationCanceledException(
                    "The download was canceled, but its incomplete file could not be removed.",
                    new AggregateException(cancellationError, cleanupError),
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(destinationPath);
                return new Result<Unit, NetworkError>.Failure(
                    new NetworkError.ConnectionFailure(ex.Message, ex.ToString()));
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                return new Result<Unit, NetworkError>.Failure(new NetworkError.ConnectionFailure(
                    $"{ex.Message} The incomplete download could not be removed: {cleanupError.Message}"));
            }
        }
    }

    private static async Task DownloadRangesAsync(HttpRangeTransfer transfer,
        RangeDownloadPlan plan,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        Options options,
        CancellationToken cancellationToken
    )
    {
        var downloadProgress = new RangeDownloadProgress(plan.TotalBytes, progress);
        using var handle = File.OpenHandle(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        // Workers write disjoint ranges directly to their final offsets in this pre-sized file.
        RandomAccess.SetLength(handle, plan.TotalBytes);

        await Parallel.ForEachAsync(
            plan.Chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.WorkerCount,
                CancellationToken = cancellationToken
            },
            async (chunk, token) => await transfer.DownloadRangeAsync(
                chunk, plan.TotalBytes, plan.Validator, handle, downloadProgress, token));
    }

    private static void ValidateOptions(Options options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WorkerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StallTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialRetryDelay, TimeSpan.Zero);
    }

    internal sealed record Options
    {
        // Fixed defaults keep resource use predictable while still overlapping network reads.
        public long ChunkSize { get; init; } = 4 * 1024 * 1024;
        public int WorkerCount { get; init; } = 4;
        public int MaxAttempts { get; init; } = 3;
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    }
}
