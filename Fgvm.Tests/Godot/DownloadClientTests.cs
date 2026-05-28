using System.Net;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

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
    public async Task GetSha512_GitHubBuildsNotFound_GitHubSucceeds_ReturnsSuccess()
    {
        const string expectedChecksum = "github_checksum_content";
        if (Release.TryParse("4.0-stable") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        var gitHubMockHandler = new Mock<HttpMessageHandler>();

        gitHubMockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.RequestUri!.ToString() ==
                    "https://github.com/godotengine/godot-builds/releases/download/4.0-stable/SHA512-SUMS.txt"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent("not found")
            });

        gitHubMockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.RequestUri!.ToString() == "https://github.com/godotengine/godot/releases/download/4.0-stable/SHA512-SUMS.txt"),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(expectedChecksum)
            });

        var downloadClient = CreateDownloadClient(gitHubMockHandler);

        var result = await downloadClient.GetSha512(release, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task GetReleaseManifest_GitHubSucceeds_ReturnsManifest()
    {
        if (Release.TryParse("4.2-dev2-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        var gitHubMockHandler = new Mock<HttpMessageHandler>();
        gitHubMockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request =>
                    request.RequestUri!.ToString() ==
                    "https://raw.githubusercontent.com/godotengine/godot-builds/main/releases/godot-4.2-dev2.json"),
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
    public async Task GetZipFile_GodotDownloadApiSucceeds_ReturnsSuccess()
    {
        if (Release.TryParse("4.6.2-stable") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        release = release with { PlatformString = "linux.x86_64" };
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://downloads.godotengine.org/?version=4.6.2&flavor=stable&slug=linux.x86_64.zip&platform=linux.x86_64")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("zip")
            });

        var downloadClient = CreateDownloadClient(mockHandler, "github-token");

        var result = await downloadClient.GetZipFile("Godot_v4.6.2-stable_linux.x86_64.zip", release, CancellationToken.None);

        var success = Assert.IsType<Result<ZipDownload, NetworkError>.Success>(result);
        await using var archive = success.Value;
        using var reader = new StreamReader(archive.Stream);
        Assert.Equal("zip", await reader.ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetZipFile_GodotDownloadApiFails_GitHubBuildsSucceeds_ReturnsSuccess()
    {
        if (Release.TryParse("4.6.2-stable") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        release = release with { PlatformString = "linux.x86_64" };
        const string filename = "Godot_v4.6.2-stable_linux.x86_64.zip";
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://downloads.godotengine.org/?version=4.6.2&flavor=stable&slug=linux.x86_64.zip&platform=linux.x86_64")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent("not found")
            });

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesBearerRequest(
                    request,
                    $"https://github.com/godotengine/godot-builds/releases/download/4.6.2-stable/{filename}",
                    "github-token")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("zip")
            });

        var downloadClient = CreateDownloadClient(mockHandler, "github-token");

        var result = await downloadClient.GetZipFile(filename, release, CancellationToken.None);

        var success = Assert.IsType<Result<ZipDownload, NetworkError>.Success>(result);
        await using var archive = success.Value;
        using var reader = new StreamReader(archive.Stream);
        Assert.Equal("zip", await reader.ReadToEndAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetZipFile_DownloadSourcesFail_ReturnsFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.NotFound, "not found"));

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<ZipDownload, NetworkError>.Failure>(result);
        var requestFailure = Assert.IsType<NetworkError.RequestFailure>(failure.Error);
        Assert.Equal((int)HttpStatusCode.NotFound, requestFailure.StatusCode);
    }

    private DownloadClient CreateDownloadClient(Mock<HttpMessageHandler> httpHandler, string? githubToken = null) =>
        new(new HttpClient(httpHandler.Object), CreateMockConfiguration(githubToken), _mockLogger.Object);

    private static Mock<HttpMessageHandler> CreateMockHttpHandler(HttpStatusCode statusCode, string content)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });

        return mockHandler;
    }

    private static Lazy<IConfiguration> CreateMockConfiguration(string? githubToken = null)
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(x => x["github:token"]).Returns(githubToken);
        return new Lazy<IConfiguration>(() => mockConfig.Object);
    }

    private static bool MatchesUnauthenticatedRequest(HttpRequestMessage request, string url) =>
        request.RequestUri?.ToString() == url && request.Headers.Authorization == null;

    private static bool MatchesBearerRequest(HttpRequestMessage request, string url, string token) =>
        request.RequestUri?.ToString() == url &&
        request.Headers.Authorization is { Scheme: "Bearer" } authorization &&
        authorization.Parameter == token;
}
