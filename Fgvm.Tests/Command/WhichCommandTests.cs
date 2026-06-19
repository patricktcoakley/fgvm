using System.Text.Json;
using Fgvm.Cli.Command;
using Fgvm.Cli.Services;
using Fgvm.Cli.ViewModels;
using Fgvm.Types;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class WhichCommandTests
{
    [Fact]
    public async Task WhichCommand_WritesJson_WhenVersionIsSet()
    {
        var executablePath = Path.Combine("/Users/test/fgvm", "installations", "4.5-stable-standard", "Godot");
        var versionService = CreateVersionService(
            new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(
                new VersionResolutionOutcome.Found(
                    executablePath,
                    Path.GetDirectoryName(executablePath)!,
                    "4.5-stable-standard",
                    false,
                    "4.5-stable-standard@linux.x86_64")));

        var console = new TestConsole();
        var command = new WhichCommand(versionService.Object, console);

        await command.Which(true);

        var json = console.Output.Trim();
        var view = JsonSerializer.Deserialize<WhichView>(json, JsonView.Options);
        Assert.True(view.HasVersion);
        Assert.Equal(executablePath, view.ExecutablePath);
    }

    [Fact]
    public async Task WhichCommand_WritesJson_WhenNoVersionSet()
    {
        var versionService = CreateVersionService(
            new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(
                new VersionResolutionError.NotFound("No current version set")));

        var console = new TestConsole();
        var command = new WhichCommand(versionService.Object, console);

        await command.Which(true);

        var json = console.Output.Trim();
        var view = JsonSerializer.Deserialize<WhichView>(json, JsonView.Options);
        Assert.False(view.HasVersion);
        Assert.Equal("No Godot version is currently set.", view.Message);
    }

    private static Mock<IVersionManagementService> CreateVersionService(
        Result<VersionResolutionOutcome.Found, VersionResolutionError> result
    )
    {
        var versionService = new Mock<IVersionManagementService>();
        versionService.Setup(x => x.ResolveEffectiveVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return versionService;
    }
}
