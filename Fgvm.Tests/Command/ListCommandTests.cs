using System.Text.Json;
using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class ListCommandTests
{
    [Fact]
    public void ListCommand_WritesJsonOutput()
    {
        var registry = CreateRegistryMock(["4.5-stable", "3.5-stable"], "3.5-stable@linux.x86_64");

        var pathService = CreatePathServiceMock().Object;
        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List(true);

        var json = console.Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var entries = JsonSerializer.Deserialize<List<ListView>>(json, JsonView.Options);
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Equal("3.5-stable", entries[0].Name);
        Assert.True(entries[0].IsDefault);
    }

    [Fact]
    public void ListCommand_WritesVerticalOutputWithoutHeading()
    {
        var registry = CreateRegistryMock(["4.7-rc2-standard", "4.7-beta5-standard", "4.6.3-stable-mono"]);

        var pathService = CreatePathServiceMock().Object;
        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List();

        var output = console.Output;
        Assert.DoesNotContain("List Of Installed Versions", output);

        var lines = SplitLines(output);
        Assert.Equal(3, lines.Length);
        Assert.Contains("4.7-rc2-standard", lines[0]);
        Assert.Contains("4.7-beta5-standard", lines[1]);
        Assert.Contains("4.6.3-stable-mono", lines[2]);
    }

    [Fact]
    public void ListCommand_PutsExactDefaultInstallationFirst()
    {
        var registry = CreateRegistryMock(
            ["4.7-rc2-standard", "4.7-beta5-standard", "4.6.3-stable-mono"],
            "4.6.3-stable-mono@linux.x86_64");

        var pathServiceMock = CreatePathServiceMock();
        var pathService = pathServiceMock.Object;

        var console = new TestConsole();
        var command = new ListCommand(registry.Object, pathService, console, NullLogger<ListCommand>.Instance);

        command.List();

        var output = console.Output;
        var markerCount = output.Split(Messages.DefaultInstallationMarkerGlyph).Length - 1;
        Assert.Equal(1, markerCount);

        var lines = SplitLines(output);
        Assert.Contains(Messages.DefaultInstallationMarkerGlyph, lines[0]);
        Assert.Contains("4.6.3-stable-mono", lines[0]);
        Assert.Contains("4.7-rc2-standard", lines[1]);
        Assert.Contains("4.7-beta5-standard", lines[2]);
    }

    private static Mock<IPathService> CreatePathServiceMock()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-list-tests", Guid.NewGuid().ToString());
        var binPath = Path.Combine(rootPath, "bin");

        var mock = new Mock<IPathService>();
        mock.SetupGet(x => x.RootPath).Returns(rootPath);
        mock.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(rootPath, "releases.json"));
        mock.SetupGet(x => x.BinPath).Returns(binPath);
        mock.SetupGet(x => x.SymlinkPath).Returns(Path.Combine(rootPath, "Godot"));
        mock.SetupGet(x => x.MacAppSymlinkPath).Returns(Path.Combine(rootPath, "Godot.app"));
        mock.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, ".log"));
        return mock;
    }

    private static string[] SplitLines(string output)
    {
        using var reader = new StringReader(output);
        var lines = new List<string>();

        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return [.. lines];
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
