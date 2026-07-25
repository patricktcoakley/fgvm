namespace Fgvm.Progress;

internal static class DownloadProgressFormatter
{
    private const double MinimumElapsedSecondsForSpeed = 0.5;

    public static string Format(long bytesDownloaded, long totalBytes, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesDownloaded);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalBytes, 0);

        var downloadedMegabytes = bytesDownloaded / (double)ByteSize.Megabyte;
        var totalMegabytes = totalBytes / (double)ByteSize.Megabyte;
        var progressText = $"{downloadedMegabytes:F1}/{totalMegabytes:F1} MB";

        if (elapsed.TotalSeconds <= MinimumElapsedSecondsForSpeed)
        {
            return progressText;
        }

        var mebibytesPerSecond = bytesDownloaded / (double)ByteSize.Mebibyte / elapsed.TotalSeconds;
        var speedText = mebibytesPerSecond >= 1.0
            ? $"{mebibytesPerSecond:F1} MiB/s"
            : $"{bytesDownloaded / (double)ByteSize.Kibibyte / elapsed.TotalSeconds:F0} KiB/s";
        return $"{progressText} • {speedText}";
    }
}
