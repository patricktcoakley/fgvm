using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;
using System.Text.Json;

namespace Fgvm.Tests.Command;

public sealed class ListCommandTests
{
    [Fact]
    public void ListCommand_WritesJsonOutput()
    {
        var registry = CreateRegistryMock(["4.5-stable", "3.5-stable"]);

        var pathService = CreatePathServiceMock().Object;
        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List(true);

        var json = console.Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var entries = JsonSerializer.Deserialize<List<ListView>>(json, JsonView.Options);
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.Name == "4.5-stable");
    }

    [Fact]
    public void ListCommand_WritesPanelOutput()
    {
        var registry = CreateRegistryMock(["4.5-stable"]);

        var pathService = CreatePathServiceMock().Object;
        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List();

        var output = console.Output;
        Assert.Contains("4.5-stable", output);
        Assert.Contains(Messages.ListPanelHeader, output);
    }

    [Fact]
    public void ListCommand_MarksOnlyExactDefaultInstallation()
    {
        var registry = CreateRegistryMock(["4.5-stable", "4.5-stable-mono"], "4.5-stable@linux.x86_64");

        var pathServiceMock = CreatePathServiceMock();
        var pathService = pathServiceMock.Object;

        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List();

        var output = console.Output;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var defaultLines = lines.Where(line => line.Contains(Messages.DefaultInstallationMarkerGlyph)).ToArray();

        Assert.Single(defaultLines);
        Assert.Contains("4.5-stable", defaultLines[0]);

        var monoLine = lines.First(line => line.Contains("4.5-stable-mono"));
        Assert.DoesNotContain(Messages.DefaultInstallationMarkerGlyph, monoLine);
    }

    private static Mock<IPathService> CreatePathServiceMock()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-list-tests", Guid.NewGuid().ToString());
        var binPath = Path.Combine(rootPath, "bin");

        var mock = new Mock<IPathService>();
        mock.SetupGet(x => x.RootPath).Returns(rootPath);
        mock.SetupGet(x => x.ConfigPath).Returns(Path.Combine(rootPath, "fgvm.ini"));
        mock.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(rootPath, "releases.json"));
        mock.SetupGet(x => x.BinPath).Returns(binPath);
        mock.SetupGet(x => x.SymlinkPath).Returns(Path.Combine(rootPath, "Godot"));
        mock.SetupGet(x => x.MacAppSymlinkPath).Returns(Path.Combine(rootPath, "Godot.app"));
        mock.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, ".log"));
        return mock;
    }

    private static Mock<IInstallationRegistry> CreateRegistryMock(string[] releaseNames, string? defaultKey = null)
    {
        var installations = releaseNames
            .Select(name => new Installation($"{name}@linux.x86_64", name, "linux.x86_64", name, null, null))
            .ToArray();

        var mock = new Mock<IInstallationRegistry>();
        mock.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));

        if (defaultKey is not null && installations.FirstOrDefault(x => x.Key == defaultKey) is { } defaultInstallation)
        {
            mock.Setup(x => x.GetDefault())
                .Returns(new Result<Installation, InstallationRegistryError>.Success(defaultInstallation));
        }
        else
        {
            mock.Setup(x => x.GetDefault())
                .Returns(new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("default")));
        }

        return mock;
    }
}
