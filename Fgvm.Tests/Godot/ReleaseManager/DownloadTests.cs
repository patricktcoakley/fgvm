using Fgvm.Godot;
using Fgvm.Types;
using Moq;

namespace Fgvm.Tests.Godot.ReleaseManager;

public class DownloadTests
{
    [Fact]
    public async Task GetZipFile_ReturnsZipDownloadFromDownloadClient()
    {
        if (Release.TryParse("4.4-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        await using var expected = new ZipDownload(new MemoryStream([1, 2, 3]), 3);

        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureDownloadClient(downloadClient =>
                downloadClient.Setup(x => x.GetZipFile("fixture.zip", release, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Result<ZipDownload, NetworkError>.Success(expected)))
            .Build();

        var result = await releaseManager.GetZipFile("fixture.zip", release, CancellationToken.None);

        var success = Assert.IsType<Result<ZipDownload, NetworkError>.Success>(result);
        Assert.Same(expected, success.Value);
    }

    [Fact]
    public async Task GetZipFile_ReturnsDownloadClientFailure()
    {
        if (Release.TryParse("4.4-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        var expected = new NetworkError.ConnectionFailure("offline");
        var releaseManager = new ReleaseManagerBuilder()
            .ConfigureDownloadClient(downloadClient =>
                downloadClient.Setup(x => x.GetZipFile("fixture.zip", release, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Result<ZipDownload, NetworkError>.Failure(expected)))
            .Build();

        var result = await releaseManager.GetZipFile("fixture.zip", release, CancellationToken.None);

        var failure = Assert.IsType<Result<ZipDownload, NetworkError>.Failure>(result);
        Assert.Same(expected, failure.Error);
    }
}
