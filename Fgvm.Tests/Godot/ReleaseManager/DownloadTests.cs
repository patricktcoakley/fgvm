using Fgvm.Godot;
using Fgvm.Godot.Download;
using Fgvm.Types;
using Moq;

namespace Fgvm.Tests.Godot.ReleaseManager;

public class DownloadTests
{
    [Fact]
    public async Task DownloadZipFileAsync_ReturnsChecksumFromDownloadClient()
    {
        if (Release.TryParse("4.4-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        const string destinationPath = "/tmp/fixture.zip";
        const string expectedChecksum = "abc123";
        var progress = new Mock<IProgress<DownloadProgress>>().Object;

        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureDownloadClient(downloadClient =>
                downloadClient.Setup(x => x.DownloadZipFileAsync(
                        "fixture.zip", release, destinationPath, progress, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Result<string, NetworkError>.Success(expectedChecksum)))
            .Build();

        var result = await releaseManager.DownloadZipFileAsync(
            "fixture.zip", release, destinationPath, progress, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task DownloadZipFileAsync_ReturnsDownloadClientFailure()
    {
        if (Release.TryParse("4.4-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        const string destinationPath = "/tmp/fixture.zip";
        var expected = new NetworkError.ConnectionFailure("offline");
        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureDownloadClient(downloadClient =>
                downloadClient.Setup(x => x.DownloadZipFileAsync(
                        "fixture.zip", release, destinationPath, null, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Result<string, NetworkError>.Failure(expected)))
            .Build();

        var result = await releaseManager.DownloadZipFileAsync(
            "fixture.zip", release, destinationPath, null, CancellationToken.None);

        var failure = Assert.IsType<Result<string, NetworkError>.Failure>(result);
        Assert.Same(expected, failure.Error);
    }
}
