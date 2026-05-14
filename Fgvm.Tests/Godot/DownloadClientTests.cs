using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;

namespace Fgvm.Tests.Godot;

public class DownloadClientTests
{
    private readonly Mock<ILogger<DownloadClient>> _mockLogger = new();
    private readonly Release _testRelease = new(4, 3, "linux_x86_64", 0, ReleaseType.Stable());

    [Fact]
    public async Task GetSha512_GitHubSucceeds_ReturnsSuccess()
    {
        const string expectedChecksum = "test_checksum_content";
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.OK, expectedChecksum));

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task GetSha512_GitHubFails_ReturnsFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.ServiceUnavailable, "GitHub down"));

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<string, NetworkError>.Failure>(result);
        var requestFailure = Assert.IsType<NetworkError.RequestFailure>(failure.Error);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, requestFailure.StatusCode);
    }

    [Fact]
    public async Task GetReleaseManifest_GitHubSucceeds_ReturnsManifest()
    {
        var release = Release.TryParse("4.2-dev2-standard")!;
        var gitHubMockHandler = new Mock<HttpMessageHandler>();
        gitHubMockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.RequestUri!.ToString() == "https://raw.githubusercontent.com/godotengine/godot-builds/main/releases/godot-4.2-dev2.json"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    """
                    {
                      "name": "4.2-dev2",
                      "version": "4.2",
                      "status": "dev2",
                      "release_date": 123,
                      "git_reference": "abc123",
                      "files": [
                        {
                          "filename": "Godot_v4.2-dev2_macos.universal.zip",
                          "checksum": "hash"
                        }
                      ]
                    }
                    """)
            });

        var downloadClient = CreateDownloadClient(gitHubMockHandler);

        var result = await downloadClient.GetReleaseManifest(release, CancellationToken.None);

        var success = Assert.IsType<Result<GodotReleaseManifest, NetworkError>.Success>(result);
        Assert.Equal("4.2-dev2", success.Value.Name);
        Assert.Equal(123, success.Value.ReleaseDate);
        Assert.Single(success.Value.Files);
        Assert.Equal("hash", success.Value.Files[0].Checksum);
    }

    [Fact]
    public async Task GetZipFile_GitHubSucceeds_ReturnsSuccess()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.OK, "zip"));

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<HttpResponseMessage, NetworkError>.Success>(result);
        Assert.Equal(HttpStatusCode.OK, success.Value.StatusCode);
    }

    [Fact]
    public async Task GetZipFile_GitHubFails_ReturnsFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.NotFound, "not found"));

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<HttpResponseMessage, NetworkError>.Failure>(result);
        var requestFailure = Assert.IsType<NetworkError.RequestFailure>(failure.Error);
        Assert.Equal((int)HttpStatusCode.NotFound, requestFailure.StatusCode);
    }

    private DownloadClient CreateDownloadClient(Mock<HttpMessageHandler> httpHandler) =>
        new DownloadClient(new HttpClient(httpHandler.Object), CreateMockConfiguration(), _mockLogger.Object);

    private static Mock<HttpMessageHandler> CreateMockHttpHandler(HttpStatusCode statusCode, string content)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });

        return mockHandler;
    }

    private static Lazy<IConfiguration> CreateMockConfiguration()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(x => x["github:token"]).Returns((string?)null);
        return new Lazy<IConfiguration>(() => mockConfig.Object);
    }
}
