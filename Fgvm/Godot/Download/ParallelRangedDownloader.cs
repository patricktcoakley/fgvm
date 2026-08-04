using System.Net;
using System.Net.Http.Headers;
using Fgvm.Types;

namespace Fgvm.Godot.Download;

/// <summary>
///     Reports download progress independent of any specific presentation format.
/// </summary>
/// <param name="BytesDownloaded">Total bytes written so far.</param>
/// <param name="TotalBytes">The full download size, or null when unknown.</param>
/// <param name="SourceUrl">The resolved source serving the response, or null for byte-progress updates.</param>
public readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes, string? SourceUrl = null);

/// <summary>
///     Chooses between a parallel range download and a sequential fallback.
/// </summary>
internal static class ParallelRangedDownloader
{
    // The initial attempt plus one restart for an entity that changed underneath it.
    private const int MaxEntityChangeAttempts = 2;

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
            // One restart covers a file replaced mid-download; an unlimited one would spin on a source that
            // changes constantly.
            for (var attempt = 1; attempt <= MaxEntityChangeAttempts; attempt++)
            {
                try
                {
                    return await AttemptDownloadAsync(
                        transfer, sourceUrl, destinationPath, progress, options, cancellationToken);
                }
                catch (Exception ex) when (attempt < MaxEntityChangeAttempts && IsEntityChanged(ex))
                {
                    File.Delete(destinationPath);
                }
            }

            throw new InvalidOperationException("Download restart started without an available attempt.");
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

    private static bool IsEntityChanged(Exception exception) => exception switch
    {
        EntityChangedException => true,
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(inner => inner is EntityChangedException),
        _ => false
    };

    private static async Task<Result<Unit, NetworkError>> AttemptDownloadAsync(HttpRangeTransfer transfer,
        string sourceUrl,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        Options options,
        CancellationToken cancellationToken
    )
    {
        // The destination is written directly, so never leave an older file in place.
        File.Delete(destinationPath);
        using var probeResponse = await transfer.ProbeAsync(cancellationToken);
        var resolvedSourceUrl = probeResponse.RequestMessage?.RequestUri?.AbsoluteUri ?? sourceUrl;
        progress?.Report(new DownloadProgress(0, null, resolvedSourceUrl));

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
                await transfer.DownloadSequentialAsync(null, destinationPath, progress, cancellationToken);
                return new Result<Unit, NetworkError>.Success(Unit.Value);

            default:
                var responseBody = await probeResponse.Content.ReadAsStringAsync(cancellationToken);
                return new Result<Unit, NetworkError>.Failure(
                    new NetworkError.RequestFailure(sourceUrl, probeResponse.StatusCode, responseBody));
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

        downloadProgress.ReportCompleted();
    }

    private static void ValidateOptions(Options options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WorkerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttemptsWithoutProgress, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTotalAttemptsPerRange, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StallTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.InitialRetryDelay, TimeSpan.Zero);
    }

    internal sealed record Options
    {
        // Fixed defaults keep resource use predictable while still overlapping network reads.
        // The chunk size was chosen by measurement: it is small enough that a typical release
        // archive yields several chunks per worker in order to stay saturated.
        public long ChunkSize { get; init; } = 8L * ByteSize.Mebibyte;
        public int WorkerCount { get; init; } = 8;
        // Probe and sequential retries make no durable progress. Ranged recovery resets this
        // budget whenever an attempt writes bytes successfully.
        public int MaxAttemptsWithoutProgress { get; init; } = 3;
        // Partial progress resets the consecutive-failure budget, but this hard cap prevents
        // a server that repeatedly sends only a few bytes from retrying indefinitely.
        public int MaxTotalAttemptsPerRange { get; init; } = 8;
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    }
}
