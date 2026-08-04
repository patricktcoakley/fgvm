using Fgvm.Godot.Download;

namespace Fgvm.Tests.Godot.Download;

public sealed class RangeDownloadProgressTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public void ReportBytesWritten_DoesNotReportBelowTheThreshold()
    {
        var recorder = new ProgressRecorder();
        var progress = new RangeDownloadProgress(10 * Mebibyte, recorder);

        progress.ReportBytesWritten(1024);

        Assert.Empty(recorder.Reported);
    }

    [Fact]
    public void ReportBytesWritten_ReportsOnceThresholdIsReached()
    {
        var recorder = new ProgressRecorder();
        var progress = new RangeDownloadProgress(10 * Mebibyte, recorder);

        progress.ReportBytesWritten(Mebibyte);

        var report = Assert.Single(recorder.Reported);
        Assert.Equal(Mebibyte, report.BytesDownloaded);
        Assert.Equal(10 * Mebibyte, report.TotalBytes);
    }

    [Fact]
    public void ReportCompleted_ReportsTheExactTotalWhenTheThresholdSwallowedTheTail()
    {
        var recorder = new ProgressRecorder();
        var totalBytes = Mebibyte + 5;
        var progress = new RangeDownloadProgress(totalBytes, recorder);

        progress.ReportBytesWritten(Mebibyte);
        progress.ReportBytesWritten(5);
        progress.ReportCompleted();

        Assert.Equal(totalBytes, recorder.Reported[^1].BytesDownloaded);
    }

    [Fact]
    public void ReportCompleted_DoesNotReportTwiceWhenTheTotalWasAlreadyReached()
    {
        var recorder = new ProgressRecorder();
        var progress = new RangeDownloadProgress(Mebibyte, recorder);

        progress.ReportBytesWritten(Mebibyte);
        progress.ReportCompleted();

        Assert.Single(recorder.Reported);
    }

    [Fact]
    public void ReportBytesWritten_IgnoresANullProgressSink()
    {
        var progress = new RangeDownloadProgress(Mebibyte, null);

        progress.ReportBytesWritten(Mebibyte);
        progress.ReportCompleted();
    }

    /// <summary>
    ///     Workers must never emit a byte count lower than one already emitted; a bar driven from these values
    ///     renders that as jumping backwards.
    /// </summary>
    [Fact]
    public async Task ReportBytesWritten_NeverGoesBackwardsUnderConcurrentWorkers()
    {
        const int workers = 8;
        const int chunksPerWorker = 64;
        const int chunkSize = 256 * 1024;
        var totalBytes = (long)workers * chunksPerWorker * chunkSize;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var recorder = new ProgressRecorder();
            var progress = new RangeDownloadProgress(totalBytes, recorder);

            await Parallel.ForEachAsync(
                Enumerable.Range(0, workers),
                async (_, _) => await Task.Run(() =>
                {
                    for (var chunk = 0; chunk < chunksPerWorker; chunk++)
                    {
                        progress.ReportBytesWritten(chunkSize);
                    }
                }));

            progress.ReportCompleted();

            var reported = recorder.Reported.Select(report => report.BytesDownloaded).ToArray();
            Assert.NotEmpty(reported);
            Assert.Equal(reported.OrderBy(bytes => bytes), reported);
            Assert.Equal(totalBytes, reported[^1]);
        }
    }

    private sealed class ProgressRecorder : IProgress<DownloadProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<DownloadProgress> _reported = [];

        public IReadOnlyList<DownloadProgress> Reported
        {
            get
            {
                lock (_gate)
                {
                    return _reported.ToArray();
                }
            }
        }

        public void Report(DownloadProgress value)
        {
            lock (_gate)
            {
                _reported.Add(value);
            }
        }
    }
}
