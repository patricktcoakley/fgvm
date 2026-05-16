using Fgvm.Cli.Command;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Moq;
using Spectre.Console.Testing;
using System.Text.Json;

namespace Fgvm.Tests.Command;

public sealed class WhichCommandTests
{
    [Fact]
    public void WhichCommand_WritesJson_WhenVersionIsSet()
    {
        var installation = new Installation("4.5-stable-standard@linux.x86_64", "4.5-stable-standard", "linux.x86_64", "4.5-stable-standard", null, null);
        var registry = new Mock<IInstallationRegistry>();
        registry.Setup(x => x.GetDefault()).Returns(new Result<Installation, InstallationRegistryError>.Success(installation));

        if (Release.TryParse("4.5-stable-standard") is not { } release)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        release = release with { OS = OS.Linux, PlatformString = "linux.x86_64" };
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(x => x.CreateRelease("4.5-stable-standard"))
            .Returns(new Result<Release, ReleaseParseError>.Success(release));

        var pathService = CreatePathService();

        var console = new TestConsole();
        var command = new WhichCommand(registry.Object, releaseManager.Object, pathService.Object, console);

        command.Which(true);

        var json = console.Output.Trim();
        var view = JsonSerializer.Deserialize<WhichView>(json, JsonView.Options);
        Assert.True(view.HasVersion);
        Assert.Equal(Path.Combine(pathService.Object.RootPath, installation.RelativePath, release.ExecName), view.ExecutablePath);
    }

    [Fact]
    public void WhichCommand_WritesJson_WhenNoVersionSet()
    {
        var registry = new Mock<IInstallationRegistry>();
        registry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("default")));

        var console = new TestConsole();
        var command = new WhichCommand(registry.Object, new Mock<IReleaseManager>().Object, CreatePathService().Object, console);

        command.Which(true);

        var json = console.Output.Trim();
        var view = JsonSerializer.Deserialize<WhichView>(json, JsonView.Options);
        Assert.False(view.HasVersion);
        Assert.Equal("No Godot version is currently set.", view.Message);
    }

    private static Mock<IPathService> CreatePathService()
    {
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns("/Users/test/fgvm");
        return pathService;
    }
}
