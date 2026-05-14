using Fgvm.Cli.Command;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;
using System.Text.Json;

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
        Assert.Contains("List Of Available Versions", output);
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

    private static SearchCommand CreateCommand(IReleaseManager releaseManager, out TestConsole console)
    {
        var pathServiceMock = new Mock<IPathService>();
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-search-tests");
        pathServiceMock.SetupGet(x => x.RootPath).Returns(rootPath);
        pathServiceMock.SetupGet(x => x.ConfigPath).Returns(Path.Combine(rootPath, "fgvm.ini"));
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
