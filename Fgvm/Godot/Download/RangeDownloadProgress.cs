namespace Fgvm.Godot.Download;

/// <summary>
///     Aggregates progress from concurrent workers and corrects it when a range is retried.
/// </summary>
internal sealed class RangeDownloadProgress(long totalBytes, IProgress<DownloadProgress>? progress)
{
    // Limit UI updates while still reporting the exact final byte count.
    private const long ReportThreshold = 1024 * 1024;
    private long _bytesDownloaded;
    private long _lastReportedBytes;

    public void Add(int byteCount)
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

    public void Subtract(long byteCount)
    {
        // A failed range is downloaded again from its first byte, so its earlier writes no longer count.
        if (byteCount != 0)
        {
            Interlocked.Add(ref _bytesDownloaded, -byteCount);
        }
    }
}
