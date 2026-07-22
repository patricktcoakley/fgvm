using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fgvm.Godot;
using Fgvm.Godot.Download;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace Fgvm.Tests.Godot;

public sealed class DownloadClientTests : IDisposable
{
    private readonly Mock<ILogger<DownloadClient>> _mockLogger = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-download-client-tests", Guid.NewGuid().ToString("N"));
    private readonly Release _testRelease = new(4, 3, "linux_x86_64", 0, ReleaseType.Stable());

    public DownloadClientTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task ListReleases_GitHubIndexSucceeds_ReturnsReleaseNamesNewestFirst()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://api.github.com/repos/godotengine/godot-builds/contents/releases")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    """
                    [
                      { "name": "godot-4.4-stable.json" },
                      { "name": "godot-4.5-dev1.json" }
                    ]
                    """)
            });

        var downloadClient = CreateDownloadClient(mockHandler);

        var result = await downloadClient.ListReleases(CancellationToken.None);

        var success = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Success>(result);
        Assert.Equal(["4.5-dev1", "4.4-stable"], success.Value);
    }

    [Fact]
    public async Task ListReleases_InvalidJson_ReturnsConnectionFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.OK, "{ nope"));

        var result = await downloadClient.ListReleases(CancellationToken.None);

        var failure = Assert.IsType<Result<IEnumerable<string>, NetworkError>.Failure>(result);
        Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
    }

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
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://github.com/godotengine/godot-builds/releases/download/4.0-stable/SHA512-SUMS.txt")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound,
                Content = new StringContent("not found")
            });

        gitHubMockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://github.com/godotengine/godot/releases/download/4.0-stable/SHA512-SUMS.txt")),
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
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    "https://raw.githubusercontent.com/godotengine/godot-builds/main/releases/godot-4.2-dev2.json")),
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
    public async Task GetReleaseManifest_InvalidJson_ReturnsConnectionFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.OK, "{ nope"));

        var result = await downloadClient.GetReleaseManifest(_testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<GodotReleaseManifest, NetworkError>.Failure>(result);
        Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
    }

    [Fact]
    public async Task GetReleaseManifest_NullJson_ReturnsConnectionFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.OK, "null"));

        var result = await downloadClient.GetReleaseManifest(_testRelease, CancellationToken.None);

        var failure = Assert.IsType<Result<GodotReleaseManifest, NetworkError>.Failure>(result);
        Assert.IsType<NetworkError.ConnectionFailure>(failure.Error);
    }

    [Fact]
    public async Task DownloadZipFileAsync_GodotDownloadApiSucceeds_ReturnsChecksumAndWritesFile()
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
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("zip")
            });

        var downloadClient = CreateDownloadClient(mockHandler);
        var destinationPath = Path.Combine(_root, "out.zip");

        var result = await downloadClient.DownloadZipFileAsync(
            "Godot_v4.6.2-stable_linux.x86_64.zip", release, destinationPath, null, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal("zip", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal(await ExpectedSha512("zip"), success.Value);
    }

    [Fact]
    public async Task DownloadZipFileAsync_GodotDownloadApiFails_GitHubBuildsSucceeds_ReturnsChecksum()
    {
        if (Release.TryParse("4.6.2-stable") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        release = release with { PlatformString = "linux.x86_64" };
        const string filename = "Godot_v4.6.2-stable_linux.x86_64.zip";
        const string primaryMirrorUrl = "https://objects.example.test/godot/primary.zip";
        const string fallbackMirrorUrl = "https://objects.example.test/godot/fallback.zip";
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
                Content = new StringContent("not found"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, primaryMirrorUrl)
            });

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => MatchesUnauthenticatedRequest(
                    request,
                    $"https://github.com/godotengine/godot-builds/releases/download/4.6.2-stable/{filename}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("zip"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, fallbackMirrorUrl)
            });

        var downloadClient = CreateDownloadClient(mockHandler);
        var destinationPath = Path.Combine(_root, "out.zip");
        var progress = new RecordingProgress();

        var result = await downloadClient.DownloadZipFileAsync(
            filename, release, destinationPath, progress, CancellationToken.None);

        var success = Assert.IsType<Result<string, NetworkError>.Success>(result);
        Assert.Equal("zip", await File.ReadAllTextAsync(destinationPath));
        Assert.Equal(await ExpectedSha512("zip"), success.Value);
        Assert.Equal(
        [
            primaryMirrorUrl,
            fallbackMirrorUrl
        ], progress.Reports.Where(report => report.SourceUrl is not null).Select(report => report.SourceUrl));
    }

    [Fact]
    public async Task DownloadZipFileAsync_DownloadSourcesFail_ReturnsFailure()
    {
        var downloadClient = CreateDownloadClient(CreateMockHttpHandler(HttpStatusCode.NotFound, "not found"));
        var destinationPath = Path.Combine(_root, "out.zip");

        var result = await downloadClient.DownloadZipFileAsync(
            _testRelease.ZipFileName, _testRelease, destinationPath, null, CancellationToken.None);

        var failure = Assert.IsType<Result<string, NetworkError>.Failure>(result);
        var requestFailure = Assert.IsType<NetworkError.RequestFailure>(failure.Error);
        Assert.Equal((int)HttpStatusCode.NotFound, requestFailure.StatusCode);
    }

    private DownloadClient CreateDownloadClient(Mock<HttpMessageHandler> httpHandler) =>
        new(new HttpClient(httpHandler.Object), _mockLogger.Object);

    private static async Task<string> ExpectedSha512(string content)
    {
        var hash = await SHA512.HashDataAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        return Convert.ToHexStringLower(hash);
    }

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

    private static bool MatchesUnauthenticatedRequest(HttpRequestMessage request, string url) =>
        request.RequestUri?.ToString() == url && request.Headers.Authorization == null;

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        private readonly Lock _lock = new();
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value)
        {
            lock (_lock)
            {
                Reports.Add(value);
            }
        }
    }
}
