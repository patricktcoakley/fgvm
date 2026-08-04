namespace Fgvm.Godot.Download;

/// <summary>
///     Aggregates durable progress from concurrent range workers.
/// </summary>
internal sealed class RangeDownloadProgress(long totalBytes, IProgress<DownloadProgress>? progress)
{
    // Limit UI updates while still reporting the exact final byte count.
    private const long ReportThreshold = ByteSize.Mebibyte;
    private readonly Lock _reportGate = new();
    private long _bytesDownloaded;
    private long _lastReportedBytes;

    public void ReportBytesWritten(int byteCount)
    {
        var total = Interlocked.Add(ref _bytesDownloaded, byteCount);
        if (progress is null)
        {
            return;
        }

        lock (_reportGate)
        {
            if (total <= _lastReportedBytes || total - _lastReportedBytes < ReportThreshold)
            {
                return;
            }

            _lastReportedBytes = total;
            progress.Report(new DownloadProgress(total, totalBytes));
        }
    }

    /// <summary>
    ///     Reports the exact final byte count once every worker has finished; the threshold above would otherwise
    ///     swallow the last partial chunk.
    /// </summary>
    public void ReportCompleted()
    {
        if (progress is null)
        {
            return;
        }

        lock (_reportGate)
        {
            if (_lastReportedBytes >= totalBytes)
            {
                return;
            }

            _lastReportedBytes = totalBytes;
            progress.Report(new DownloadProgress(totalBytes, totalBytes));
        }
    }
}
