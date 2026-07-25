using Fgvm.Progress;

namespace Fgvm.Tests.Progress;

public sealed class DownloadProgressFormatterTests
{
    [Theory]
    [InlineData(5_000_000L, 10_000_000L, 1.0, "5.0/10.0 MB • 4.8 MiB/s")]
    [InlineData(524_288L, 1_000_000L, 1.0, "0.5/1.0 MB • 512 KiB/s")]
    [InlineData(5_000_000L, 10_000_000L, 0.5, "5.0/10.0 MB")]
    public void Format_DistinguishesDecimalProgressFromBinaryThroughput(long bytesDownloaded,
        long totalBytes,
        double elapsedSeconds,
        string expected
    )
    {
        var result = DownloadProgressFormatter.Format(
            bytesDownloaded, totalBytes, TimeSpan.FromSeconds(elapsedSeconds));

        Assert.Equal(expected, result);
    }
}
