namespace Fgvm.Godot.Download;

/// <summary>
///     Aggregates durable progress from concurrent range workers.
/// </summary>
internal sealed class RangeDownloadProgress(long totalBytes, IProgress<DownloadProgress>? progress)
{
    // Limit UI updates while still reporting the exact final byte count.
    private const long ReportThreshold = ByteSize.Mebibyte;
    private long _bytesDownloaded;
    private long _lastReportedBytes;

    public void ReportBytesWritten(int byteCount)
    {
        var total = Interlocked.Add(ref _bytesDownloaded, byteCount);
        if (progress is null)
        {
            return;
        }

        var last = Interlocked.Read(ref _lastReportedBytes);
        if (total - last < ReportThreshold && total != totalBytes)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastReportedBytes, total, last) == last)
        {
            progress.Report(new DownloadProgress(total, totalBytes));
        }
    }
}
