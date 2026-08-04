using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Fgvm.Extensions;
using Fgvm.Godot.Download;
using Fgvm.Types;

namespace Fgvm.Tests.Godot.Download;

public sealed class ParallelRangedDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-ranged-download-tests", Guid.NewGuid().ToString("N"));

    public ParallelRangedDownloaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task DownloadAsync_ServerHonorsRange_SingleWorker_StillFetchesEverythingSequentially()
    {
        var expected = CreateRandomBytes(6 * 1024 * 1024 + 12345);
        var handler = new RangeAwareHandler(expected, honorsRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        // One probe plus one request for the remainder.
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ServerHonorsRange_SingleWorker_FileSmallerThanProbeChunk_NeedsOnlyOneRequest()
    {
        var expected = CreateRandomBytes(2 * 1024 * 1024); // smaller than the 4MB probe chunk
        var handler = new RangeAwareHandler(expected, honorsRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ServerHonorsRange_MultipleWorkers_WritesCorrectBytesAcrossAllChunks()
    {
        // 20 MiB + 777 bytes produces one probe and five fan-out requests.
        var expected = CreateRandomBytes(20 * 1024 * 1024 + 777);
        var handler = new RangeAwareHandler(expected, honorsRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(6, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ServerIgnoresRange_FallsBackToSequentialSingleRequest()
    {
        var expected = CreateRandomBytes(2 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: false);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None);

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        // The 200 response to the probe is reused.
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_SequentialResponseStalls_RestartsDownload()
    {
        var expected = CreateRandomBytes(2 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: false, stallOnRequestNumber: 1);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(stallTimeout: TimeSpan.FromMilliseconds(100)));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ProbeReturns206WithUnusableContentRange_FallsBackToFreshSequentialRequest()
    {
        // Without a total length, the remaining ranges cannot be planned safely.
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, malformedContentRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None);

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, handler.NonRangeRequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ProbeHasNoValidator_FallsBackToFreshSequentialRequest()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, includeValidator: false);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None);

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, handler.NonRangeRequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ProbeReturnsError_ReturnsFailureWithoutCreatingFile()
    {
        var handler = new RangeAwareHandler([], honorsRange: true, forceStatusCode: HttpStatusCode.RequestedRangeNotSatisfiable);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None);

        var failure = Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        Assert.IsType<NetworkError.RequestFailure>(failure.Error);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_CancelledWhileWorkerInFlight_WaitsForWorkerBeforeReturning()
    {
        // The handler takes 300 ms to unwind; DownloadAsync must wait before disposing shared state.
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, cancellationLingerDelay: TimeSpan.FromMilliseconds(300));
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");
        using var cts = new CancellationTokenSource();

        var downloadTask = ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, cts.Token,
            TestOptions(workerCount: 4));

        await Task.Delay(50);
        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloadTask);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds >= 250,
            $"Expected DownloadAsync to wait for the in-flight worker's ~300ms lingering cleanup before " +
            $"returning, but it returned after only {stopwatch.ElapsedMilliseconds}ms.");
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_ProbeConnectionThrows_ReturnsConnectionFailure()
    {
        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions());

        var failure = Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        var connectionFailure = Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
        Assert.Contains("Simulated connection failure", connectionFailure.Details);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_ProbeFailsTransiently_RetriesWithinDownloader()
    {
        var expected = CreateRandomBytes(2 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, failOnRequestNumber: 1);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions());

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ChunkReturnsFewerBytesThanContentRangePromised_ReturnsFailure()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, truncateChunkBody: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4));

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_FanOutChunkReturnsNon206_ReturnsFailure()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, failChunkRequestsAfterProbe: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4, maxAttemptsWithoutProgress: 2));

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_FanOutChunkReturnsWrongByteRange_ReturnsFailure()
    {
        // A shifted response has the expected length but belongs at a different file offset.
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, mismatchedContentRangeOnFanOut: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4));

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
    }

    [Fact]
    public async Task DownloadAsync_ProbeHasStrongETag_FanOutUsesIfRange()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, requireIfRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(1, handler.IfRangeRequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ProbeHasLastModified_FanOutUsesDateIfRange()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var lastModified = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var handler = new RangeAwareHandler(
            expected,
            honorsRange: true,
            requireIfRange: true,
            includeValidator: false,
            lastModified: lastModified);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(1, handler.IfRangeRequestCount);
    }

    [Fact]
    public async Task DownloadAsync_OneChunkStalls_RetriesThatChunkInsteadOfFailingTheWholeDownload()
    {
        // Retry a stalled range without restarting completed chunks.
        var expected = CreateRandomBytes(4 * 1024 * 1024 + 12 * 4 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, stallOnRequestNumber: 3);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4, stallTimeout: TimeSpan.FromMilliseconds(100)));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_OneChunkStallsBeforeHeaders_RetriesThatChunk()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, headerStallOnRequestNumber: 2);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1, stallTimeout: TimeSpan.FromMilliseconds(100)));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_ChunkSucceedsOnThirdAttempt_CompletesDownload()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, failOnRequestNumbers: [2, 3]);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(4, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_MarksEveryRequestSelfRetrying_SoOuterHandlersDoNotMultiplyTheBudget(bool honorsRange)
    {
        // The sequential fallback carries no Range header, so the marker is what keeps an outer retry handler off it.
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 2));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.True(handler.RequestCount > 0);
        Assert.Equal(0, handler.UnmarkedRequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RangedDownload_UsesConfiguredFourWorkers()
    {
        // Fixed request latency makes throughput scale with concurrency.
        var expected = CreateRandomBytes(4 * 1024 * 1024 + 12 * 4 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true, requestLatency: TimeSpan.FromMilliseconds(20));
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions());

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(4, handler.PeakConcurrentRequests);
    }

    [Fact]
    public async Task DownloadAsync_OneChunkExhaustsRetryBudget_StopsBeforeAttemptingEveryQueuedChunk()
    {
        var expected = CreateRandomBytes(4 * 1024 * 1024 + 30 * 4 * 1024 * 1024);
        var failingChunkStart = 4 * 1024 * 1024 + 13 * 4 * 1024 * 1024;
        var handler = new RangeAwareHandler(
            expected, honorsRange: true, requestLatency: TimeSpan.FromMilliseconds(30), failOnStartByte: failingChunkStart);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 4, maxAttemptsWithoutProgress: 2));

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        // In-flight chunks may finish, but most remaining chunks should never start.
        Assert.True(handler.RequestCount < 25,
            $"Expected fail-fast to stop well short of every chunk being attempted, but {handler.RequestCount} requests were made.");
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgress_UpToExactTotalBytes()
    {
        var expected = CreateRandomBytes(6 * 1024 * 1024);
        var handler = new RangeAwareHandler(expected, honorsRange: true);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");
        var progress = new RecordingProgress();

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, progress, CancellationToken.None,
            TestOptions(workerCount: 4));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Contains(progress.Reports, report => report.SourceUrl == "https://example.test/file");
        var byteReports = progress.Reports.Where(report => report.SourceUrl is null).ToList();
        Assert.NotEmpty(byteReports);
        Assert.All(byteReports, report => Assert.Equal(expected.Length, report.TotalBytes));
        Assert.Equal(expected.Length, byteReports[^1].BytesDownloaded);
    }

    [Fact]
    public async Task DownloadAsync_ChunkPartiallySucceedsBeforeFailing_RetryDoesNotDoubleCountProgress()
    {
        // Request 2 returns a truncated body after reporting bytes; request 3 succeeds.
        var expected = CreateRandomBytes(6 * 1024 * 1024 + 12345);
        var handler = new RangeAwareHandler(expected, honorsRange: true, truncateOnRequestNumber: 2);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");
        var progress = new RecordingProgress();

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, progress, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        var byteReports = progress.Reports.Where(report => report.SourceUrl is null).ToList();
        Assert.All(byteReports, report => Assert.True(report.BytesDownloaded <= report.TotalBytes,
            $"Reported {report.BytesDownloaded} bytes downloaded, exceeding total {report.TotalBytes}."));
        Assert.Equal(expected.Length, byteReports[^1].BytesDownloaded);
        Assert.Equal(4 * 1024 * 1024, handler.RequestedRanges[1].From);
        Assert.Equal(expected.Length - 1, handler.RequestedRanges[2].From);
    }

    [Fact]
    public async Task DownloadAsync_MultiplePartialFailures_ResumeBeyondConsecutiveAttemptBudget()
    {
        var expected = CreateRandomBytes(12 * 1024 * 1024);
        var handler = new RangeAwareHandler(
            expected, honorsRange: true, truncateHalfOnRequestNumbers: [2, 3, 4]);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(6, handler.RequestCount);
        Assert.Equal(
            [4L * 1024 * 1024, 6L * 1024 * 1024, 7L * 1024 * 1024, 15L * 512 * 1024],
            handler.RequestedRanges.Skip(1).Take(4).Select(range => range.From));
    }

    [Fact]
    public async Task DownloadAsync_EveryAttemptMakesPartialProgress_StopsAtTotalAttemptCap()
    {
        var expected = CreateRandomBytes(8 * 1024 * 1024);
        var handler = new RangeAwareHandler(
            expected, honorsRange: true, truncateHalfOnRequestNumbers: [2, 3, 4, 5, 6, 7, 8, 9]);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        Assert.Equal(9, handler.RequestCount);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_RangeBodyExceedsDeclaredRange_DoesNotWritePastChunkBoundary()
    {
        var expected = CreateRandomBytes(12 * 1024 * 1024);
        var handler = new RangeAwareHandler(
            expected, honorsRange: true, appendExtraByteOnRequestNumber: 2);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions(workerCount: 1));

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public void Options_DefaultsToEightWorkersAndEightMiBChunks()
    {
        var options = new ParallelRangedDownloader.Options();

        Assert.Equal(8, options.WorkerCount);
        Assert.Equal(8L * ByteSize.Mebibyte, options.ChunkSize);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static HttpRequestMessage CreateRequest(RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/file");
        if (range is not null)
        {
            request.Headers.Range = range;
        }

        return request;
    }

    private static byte[] CreateRandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    // A source that replaces the file mid-download answers the If-Range requests with the whole new file, so
    // the only correct recovery is to start over rather than mix the two entities.
    [Fact]
    public async Task DownloadAsync_RestartsAndSucceeds_WhenTheFileChangesMidDownload()
    {
        var oldContent = CreateRandomBytes(20 * 1024 * 1024 + 777);
        var newContent = CreateRandomBytes(17 * 1024 * 1024 + 31);
        var handler = new EntityChangingHandler(oldContent, newContent);
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions());

        Assert.IsType<Result<Unit, NetworkError>.Success>(result);
        Assert.True(handler.Changed);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task DownloadAsync_FailsWithoutLoopingForever_WhenTheFileKeepsChanging()
    {
        var handler = new AlwaysChangingHandler(CreateRandomBytes(20 * 1024 * 1024 + 777));
        using var httpClient = new HttpClient(handler);
        var destinationPath = Path.Combine(_root, "out.bin");

        var result = await ParallelRangedDownloader.DownloadAsync(
            httpClient, "https://example.test/file", CreateRequest, destinationPath, null, CancellationToken.None,
            TestOptions());

        Assert.IsType<Result<Unit, NetworkError>.Failure>(result);
        Assert.False(File.Exists(destinationPath));
    }

    private static ParallelRangedDownloader.Options TestOptions(int workerCount = 4,
        int maxAttemptsWithoutProgress = 3,
        TimeSpan? stallTimeout = null
    ) =>
        new()
        {
            ChunkSize = 4 * ByteSize.Mebibyte,
            WorkerCount = workerCount,
            MaxAttemptsWithoutProgress = maxAttemptsWithoutProgress,
            StallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30),
            InitialRetryDelay = TimeSpan.Zero
        };

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        private readonly Lock _lock = new();
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value)
        {
            lock (_lock)
            {
                Reports.Add(value);
            }
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated connection failure.");
    }

    // Response body that blocks until its read is canceled.
    private sealed class HangingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new HangingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            { }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0; // Unreachable: the delay only ever completes via cancellation.
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    ///     Serves the old entity until the first If-Range request, answers that one with the whole new
    ///     file as a real server does when the validator stops matching, then serves the new one.
    /// </summary>
    private sealed class EntityChangingHandler(byte[] oldContent, byte[] newContent) : HttpMessageHandler
    {
        private volatile bool _changed;

        public bool Changed => _changed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.IfRange is not null && !_changed)
            {
                _changed = true;
                var replaced = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newContent) };
                replaced.Headers.ETag = new EntityTagHeaderValue("\"new\"");
                return Task.FromResult(replaced);
            }

            return Task.FromResult(CreateRangeResponse(request, _changed ? newContent : oldContent, _changed ? "\"new\"" : "\"old\""));
        }
    }

    /// <summary>
    ///     Answers every If-Range request with the whole file, so the entity never settles.
    /// </summary>
    private sealed class AlwaysChangingHandler(byte[] content) : HttpMessageHandler
    {
        private int _generation;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.IfRange is not null)
            {
                var replaced = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
                replaced.Headers.ETag = new EntityTagHeaderValue($"\"gen-{Interlocked.Increment(ref _generation)}\"");
                return Task.FromResult(replaced);
            }

            return Task.FromResult(CreateRangeResponse(
                request, content, $"\"gen-{Interlocked.Increment(ref _generation)}\""));
        }
    }

    private static HttpResponseMessage CreateRangeResponse(HttpRequestMessage request, byte[] content, string etag)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        if (range is null)
        {
            var whole = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            whole.Headers.ETag = new EntityTagHeaderValue(etag);
            return whole;
        }

        var from = (int)(range.From ?? 0);
        var to = (int)Math.Min(range.To ?? content.Length - 1, content.Length - 1);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(content[from..(to + 1)])
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private sealed class RangeAwareHandler(
        byte[] content,
        bool honorsRange,
        HttpStatusCode? forceStatusCode = null,
        bool truncateChunkBody = false,
        bool failChunkRequestsAfterProbe = false,
        TimeSpan? requestLatency = null,
        int? failOnRequestNumber = null,
        bool malformedContentRange = false,
        TimeSpan? cancellationLingerDelay = null,
        bool mismatchedContentRangeOnFanOut = false,
        int? stallOnRequestNumber = null,
        int? headerStallOnRequestNumber = null,
        int[]? failOnRequestNumbers = null,
        long? failOnStartByte = null,
        int? truncateOnRequestNumber = null,
        int[]? truncateHalfOnRequestNumbers = null,
        int? appendExtraByteOnRequestNumber = null,
        bool requireIfRange = false,
        bool includeValidator = true,
        DateTimeOffset? lastModified = null
    ) : HttpMessageHandler
    {
        private int _inFlightRequests;
        private int _peakConcurrentRequests;
        private int _requestCount;
        private int _ifRangeRequestCount;
        private int _nonRangeRequestCount;
        private int _unmarkedRequestCount;
        private readonly Lock _rangesLock = new();
        private readonly List<(long? From, long? To)> _requestedRanges = [];
        public int IfRangeRequestCount => _ifRangeRequestCount;
        public int NonRangeRequestCount => _nonRangeRequestCount;
        public int UnmarkedRequestCount => _unmarkedRequestCount;
        public int RequestCount => _requestCount;
        public int PeakConcurrentRequests => _peakConcurrentRequests;
        public IReadOnlyList<(long? From, long? To)> RequestedRanges
        {
            get
            {
                lock (_rangesLock)
                {
                    return _requestedRanges.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);

            if (!request.IsSelfRetrying)
            {
                Interlocked.Increment(ref _unmarkedRequestCount);
            }

            if (forceStatusCode is { } status)
            {
                return new HttpResponseMessage(status);
            }

            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range is not null)
            {
                lock (_rangesLock)
                {
                    _requestedRanges.Add((range.From, range.To));
                }
            }

            if (range is null)
            {
                Interlocked.Increment(ref _nonRangeRequestCount);
            }

            if (request.Headers.IfRange is not null)
            {
                Interlocked.Increment(ref _ifRangeRequestCount);
            }

            if (!honorsRange || range is null)
            {
                var sequentialBodyLength = truncateOnRequestNumber == requestNumber
                    ? Math.Max(1, content.Length - 1)
                    : content.Length;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = stallOnRequestNumber == requestNumber
                        ? new HangingContent()
                        : new ByteArrayContent(content.AsSpan(0, sequentialBodyLength).ToArray())
                };
            }

            if (failChunkRequestsAfterProbe && requestNumber > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var ifRangeMatches = lastModified is { } expectedDate
                ? request.Headers.IfRange?.Date == expectedDate
                : request.Headers.IfRange?.EntityTag?.Tag == "\"fixture-v1\"";
            if (requireIfRange && requestNumber > 1 && !ifRangeMatches)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            if (headerStallOnRequestNumber == requestNumber)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (requestLatency is { } latency && requestNumber > 1)
            {
                await SimulateServiceTimeAsync(latency, cancellationToken);
            }

            if (cancellationLingerDelay is { } lingerDelay && requestNumber > 1)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Keep the request alive briefly after cancellation to expose cleanup races.
                    await Task.Delay(lingerDelay, CancellationToken.None);
                    throw;
                }
            }

            if (failOnRequestNumber == requestNumber || failOnRequestNumbers?.Contains(requestNumber) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            // Fail both attempts for the selected range.
            if (failOnStartByte == range.From)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var from = (int)(range.From ?? 0);
            var to = (int)Math.Min(range.To ?? content.Length - 1, content.Length - 1);
            var length = to - from + 1;
            var bodyLength = truncateChunkBody
                ? Math.Max(0, length - 1)
                : truncateHalfOnRequestNumbers?.Contains(requestNumber) == true
                    ? Math.Max(1, length / 2)
                    : truncateOnRequestNumber == requestNumber
                        ? Math.Max(1, length - 1)
                        : length;
            var body = content.AsSpan(from, bodyLength).ToArray();
            if (appendExtraByteOnRequestNumber == requestNumber)
            {
                body = [.. body, 0xFF];
            }

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = stallOnRequestNumber == requestNumber
                    ? new HangingContent()
                    : new ByteArrayContent(body)
            };
            response.Content.Headers.ContentRange = malformedContentRange
                ? new ContentRangeHeaderValue(from, from + length - 1) // "bytes 0-N/*" - total length unknown
                : mismatchedContentRangeOnFanOut && requestNumber > 1
                    // Preserve length while shifting the claimed range by one byte.
                    ? new ContentRangeHeaderValue(from + 1, from + length, content.Length)
                    : new ContentRangeHeaderValue(from, from + length - 1, content.Length);
            if (includeValidator)
            {
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
            }

            response.Content.Headers.LastModified = lastModified;

            return response;
        }

        private async Task SimulateServiceTimeAsync(TimeSpan latency, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlightRequests);
            InterlockedMax(ref _peakConcurrentRequests, current);
            try
            {
                await Task.Delay(latency, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightRequests);
            }
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int initial;
            do
            {
                initial = location;
                if (value <= initial)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref location, value, initial) != initial);
        }
    }
}
