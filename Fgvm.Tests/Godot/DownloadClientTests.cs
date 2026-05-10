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
    private readonly Mock<ILogger<GitHubClient>> _mockGitHubLogger;
    private readonly Mock<ILogger<DownloadClient>> _mockLogger;
    private readonly Mock<ILogger<TuxFamilyClient>> _mockTuxFamilyLogger;
    private readonly Release _testRelease;

    public DownloadClientTests()
    {
        _mockLogger = new Mock<ILogger<DownloadClient>>();
        _mockGitHubLogger = new Mock<ILogger<GitHubClient>>();
        _mockTuxFamilyLogger = new Mock<ILogger<TuxFamilyClient>>();
        _testRelease = new Release(4, 3, "linux_x86_64", 0, ReleaseType.Stable());
    }

    [Fact]
    public async Task GetSha512_GitHubSucceeds_ReturnsSuccess()
    {
        const string expectedChecksum = "test_checksum_content";
        var mockHttpHandler = CreateMockHttpHandler(HttpStatusCode.OK, expectedChecksum);
        var httpClient = new HttpClient(mockHttpHandler.Object);
        var gitHubClient = new GitHubClient(httpClient, CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(new Mock<HttpMessageHandler>().Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task GetSha512_GitHubFails_TuxFamilySucceeds_ReturnsSuccess()
    {
        const string expectedChecksum = "test_checksum_content";
        var gitHubMockHandler = CreateMockHttpHandler(HttpStatusCode.ServiceUnavailable, "GitHub down");
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.OK, expectedChecksum);

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task GetSha512_BothFail_ReturnsAllSourcesFailed()
    {
        var gitHubMockHandler = CreateMockHttpHandler(HttpStatusCode.ServiceUnavailable, "GitHub down");
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.NotFound, "Not found");

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<string, NetworkError>.Failure>(result);
        var allFailed = Assert.IsType<NetworkError.AllSourcesFailed>(failure.Error);
        Assert.Equal(2, allFailed.Errors.Count);
    }

    [Fact]
    public async Task GetSha512_GitHubThrowsException_TuxFamilySucceeds_ReturnsSuccess()
    {
        const string expectedChecksum = "test_checksum_content";
        var gitHubMockHandler = CreateMockHttpHandlerThrowsException();
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.OK, expectedChecksum);

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal(expectedChecksum, success.Value);
    }

    [Fact]
    public async Task GetSha512_BothThrowException_ReturnsAllSourcesFailed()
    {
        var gitHubMockHandler = CreateMockHttpHandlerThrowsException();
        var tuxFamilyMockHandler = CreateMockHttpHandlerThrowsException();

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetSha512(_testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<string, NetworkError>.Failure>(result);
        var allFailed = Assert.IsType<NetworkError.AllSourcesFailed>(failure.Error);
        Assert.Equal(2, allFailed.Errors.Count);
        Assert.All(allFailed.Errors, error => Assert.IsType<NetworkError.ConnectionFailure>(error));
    }

    [Fact]
    public async Task GetZipFile_GitHubSucceeds_ReturnsSuccess()
    {
        var gitHubMockHandler = CreateMockHttpHandler(HttpStatusCode.OK, "zip");
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.NotFound, "not found");

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<HttpResponseMessage, NetworkError>.Success>(result);
        Assert.Equal(HttpStatusCode.OK, success.Value.StatusCode);
    }

    [Fact]
    public async Task GetZipFile_GitHubFails_TuxFamilySucceeds_ReturnsSuccess()
    {
        var gitHubMockHandler = CreateMockHttpHandler(HttpStatusCode.ServiceUnavailable, "GitHub down");
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.OK, "zip");

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var success = Assert.IsType<Result<HttpResponseMessage, NetworkError>.Success>(result);
        Assert.Equal(HttpStatusCode.OK, success.Value.StatusCode);
    }

    [Fact]
    public async Task GetZipFile_BothFail_ReturnsAllSourcesFailed()
    {
        var gitHubMockHandler = CreateMockHttpHandler(HttpStatusCode.ServiceUnavailable, "GitHub down");
        var tuxFamilyMockHandler = CreateMockHttpHandler(HttpStatusCode.NotFound, "not found");

        var gitHubClient = new GitHubClient(new HttpClient(gitHubMockHandler.Object), CreateMockConfiguration(), _mockGitHubLogger.Object);
        var tuxFamilyClient = new TuxFamilyClient(new HttpClient(tuxFamilyMockHandler.Object), _mockTuxFamilyLogger.Object);
        var downloadClient = new DownloadClient(gitHubClient, tuxFamilyClient, _mockLogger.Object);

        var result = await downloadClient.GetZipFile(_testRelease.ZipFileName, _testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<HttpResponseMessage, NetworkError>.Failure>(result);
        var allFailed = Assert.IsType<NetworkError.AllSourcesFailed>(failure.Error);
        Assert.Equal(_testRelease.ZipFileName, allFailed.Resource);
        Assert.Equal(2, allFailed.Errors.Count);
    }

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

    private static Mock<HttpMessageHandler> CreateMockHttpHandlerThrowsException()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        return mockHandler;
    }

    private static Lazy<IConfiguration> CreateMockConfiguration()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(x => x["github:token"]).Returns((string?)null);
        return new Lazy<IConfiguration>(() => mockConfig.Object);
    }
}
