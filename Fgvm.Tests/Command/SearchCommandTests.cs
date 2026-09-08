using System.Net;
using System.Text.Json;
using Fgvm.Cli.Command;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class SearchCommandTests
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonView.Options;

    [Fact]
    public async Task SearchCommand_WritesJsonOutput()
    {
        var releases = new[] { "4.5-stable-standard", "4.5-stable-mono" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), It.IsAny<ReleaseFetchMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(releases));

        var command = CreateCommand(releaseManager.Object, out var console);

        await command.Search(true);

        var json = console.Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var entries = JsonSerializer.Deserialize<List<RemoteReleaseView>>(json, SerializerOptions);
        Assert.NotNull(entries);
        Assert.Equal(releases.Length, entries.Count);
        Assert.Contains(entries, entry => entry.Name == "4.5-stable-standard");
    }

    [Fact]
    public async Task SearchCommand_WritesPanelOutput()
    {
        var releases = new[] { "4.5-stable-standard" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), It.IsAny<ReleaseFetchMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(releases));

        var command = CreateCommand(releaseManager.Object, out var console);

        await command.Search();

        var output = console.Output;
        Assert.Contains("4.5-stable-standard", output);
    }

    [Fact]
    public async Task SearchCommand_ForwardsEveryQueryWordToTheReleaseManager()
    {
        var query = new[] { "4.6", "rc" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(query, ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.6.2-rc2"]));

        var command = CreateCommand(releaseManager.Object, out _);

        await command.Search(cancellationToken: CancellationToken.None, query: query);

        releaseManager.Verify(
            x => x.SearchRemoteReleases(query, ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchCommand_ForwardsQueryAlongsideItsOptions()
    {
        var query = new[] { "4.6", "rc" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(query, ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success(["4.6.2-rc2"]));

        var command = CreateCommand(releaseManager.Object, out var console);

        await command.Search(true, true, CancellationToken.None, query);

        releaseManager.Verify(
            x => x.SearchRemoteReleases(query, ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()),
            Times.Once);

        var entries = JsonSerializer.Deserialize<List<RemoteReleaseView>>(console.Output.Trim(), SerializerOptions);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries);
        Assert.Equal("4.6.2-rc2", entry.Name);
    }

    [Fact]
    public async Task SearchCommand_NoCache_ForcesRemoteFetch()
    {
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Success([]));

        var command = CreateCommand(releaseManager.Object, out _);

        await command.Search(noCache: true);

        releaseManager.Verify(
            x => x.SearchRemoteReleases(It.IsAny<string[]>(), ReleaseFetchMode.ForceRemote, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchCommand_ManifestRefreshFailure_WritesWarningAndCachedOutput()
    {
        var releases = new[] { "4.5-stable" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Failure(new NetworkError.ManifestRefreshFailure(releases)));

        var command = CreateCommand(releaseManager.Object, out var console);

        await command.Search();

        Assert.Contains("Could not refresh the release cache", console.Output);
        Assert.Contains("4.5-stable", console.Output);
    }

    [Fact]
    public async Task SearchCommand_ManifestRefreshFailure_WithJson_WritesCachedJsonWithoutWarning()
    {
        var releases = new[] { "4.5-stable" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), ReleaseFetchMode.UseCache, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Failure(new NetworkError.ManifestRefreshFailure(releases)));

        var command = CreateCommand(releaseManager.Object, out var console);

        await command.Search(json: true);

        var json = console.Output.Trim();
        Assert.DoesNotContain("Could not refresh the release cache", json);
        Assert.DoesNotContain("[orange1]", json);

        var entries = JsonSerializer.Deserialize<List<RemoteReleaseView>>(json, SerializerOptions);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries);
        Assert.Equal("4.5-stable", entry.Name);
    }

    [Fact]
    public async Task SearchCommand_RequestFailure_ReportsBothTheStatusNumberAndItsName()
    {
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.SearchRemoteReleases(It.IsAny<string[]>(), It.IsAny<ReleaseFetchMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IEnumerable<string>, NetworkError>.Failure(
                new NetworkError.RequestFailure(
                    "https://[::1]/godot-builds.git/info/refs?service=git-upload-pack",
                    HttpStatusCode.Forbidden,
                    "API rate limit exceeded [shared IP]")));

        var command = CreateCommand(releaseManager.Object, out var console);
        console.Profile.Width = 500;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => command.Search());

        Assert.Contains("403 (Forbidden)", error.Message);
        Assert.Contains("API rate limit exceeded [shared IP]", error.Message, StringComparison.Ordinal);
        Assert.Contains("403 (Forbidden)", console.Output, StringComparison.Ordinal);
        Assert.Contains("https://[::1]", console.Output, StringComparison.Ordinal);
        Assert.Contains("API rate limit exceeded [shared IP]", console.Output, StringComparison.Ordinal);
    }

    private static SearchCommand CreateCommand(IReleaseManager releaseManager, out TestConsole console)
    {
        var pathServiceMock = new Mock<IPathService>();
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-search-tests");
        pathServiceMock.SetupGet(x => x.RootPath).Returns(rootPath);
        pathServiceMock.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(rootPath, "releases.json"));
        pathServiceMock.SetupGet(x => x.BinPath).Returns(Path.Combine(rootPath, "bin"));
        pathServiceMock.SetupGet(x => x.SymlinkPath).Returns(Path.Combine(rootPath, "Godot"));
        pathServiceMock.SetupGet(x => x.MacAppSymlinkPath).Returns(Path.Combine(rootPath, "Godot.app"));
        pathServiceMock.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, ".log"));

        console = new TestConsole();
        var logger = NullLogger<SearchCommand>.Instance;
        return new SearchCommand(releaseManager, pathServiceMock.Object, console, logger);
    }
}
