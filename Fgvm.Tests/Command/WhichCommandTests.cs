using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Error;
using Fgvm.Types;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class WhichCommandTests
{
    [Fact]
    public async Task WhichCommand_WritesPath_WhenVersionIsSet()
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

        await command.Which();

        Assert.Equal(executablePath, console.Output.Trim());
    }

    [Fact]
    public async Task WhichCommand_WhenNoVersionSet_WritesErrorAndFails()
    {
        var versionService = CreateVersionService(
            new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(
                new VersionResolutionError.NotFound("No current version set")));

        var console = new TestConsole();
        var command = new WhichCommand(versionService.Object, console);

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => command.Which());

        Assert.Equal(ExitCodes.GeneralError, exception.ExitCode);
        Assert.Empty(console.Output);
    }

    [Fact]
    public async Task WhichCommand_WithQuery_WritesResolvedInstalledVersionPath()
    {
        var executablePath = Path.Combine("/Users/test/fgvm", "installations", "4.6.2-stable-mono", "Godot");
        var query = new[] { "4.6", "mono" };
        var versionService = new Mock<IVersionManagementService>();
        versionService.Setup(x => x.ResolveInstalledVersionAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(
                new VersionResolutionOutcome.Found(
                    executablePath,
                    Path.GetDirectoryName(executablePath)!,
                    "4.6.2-stable-mono",
                    false,
                    "4.6.2-stable-mono@linux.x86_64")));

        var console = new TestConsole();
        var command = new WhichCommand(versionService.Object, console);

        await command.Which(query: query);

        Assert.Equal(executablePath, console.Output.Trim());
        versionService.Verify(x => x.ResolveEffectiveVersionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhichCommand_WithQuery_WritesNoMatchMessage()
    {
        var query = new[] { "9.9" };
        var versionService = new Mock<IVersionManagementService>();
        versionService.Setup(x => x.ResolveInstalledVersionAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure(
                new VersionResolutionError.NotFound("9.9")));

        var console = new TestConsole();
        var command = new WhichCommand(versionService.Object, console);

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => command.Which(query: query));

        Assert.Equal(ExitCodes.GeneralError, exception.ExitCode);
        Assert.Empty(console.Output);
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
