using System.Text.Json;
using Fgvm.Cli.Command;
using Fgvm.Cli.Services;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class TemplateCommandTests
{
    [Fact]
    public async Task Install_ForwardsQueryAndForceFlag()
    {
        var query = new[] { "4.6", "mono" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.InstallAsync(query, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled("4.6.stable.mono", "/templates/4.6.stable.mono")));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.Install(true, CancellationToken.None, query);

        orchestrator.Verify(x => x.InstallAsync(query, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAlias_ForwardsQueryAndForceFlag()
    {
        var query = new[] { "4.6", "mono" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.InstallAsync(query, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled("4.6.stable.mono", "/templates/4.6.stable.mono")));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.InstallAlias(true, CancellationToken.None, query);

        orchestrator.Verify(x => x.InstallAsync(query, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void List_WritesJsonOutput()
    {
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.List())
            .Returns(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(
            [
                new TemplateInstallation(
                    "4.6.3.stable.mono",
                    "4.6.3-stable-mono",
                    RuntimeEnvironment.Mono,
                    "/templates/4.6.3.stable.mono",
                    null)
            ]));

        var command = CreateCommand(orchestrator.Object, out var console);

        command.List(true);

        var entries = JsonSerializer.Deserialize<List<TemplateListView>>(console.Output.Trim(), JsonView.Options);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries);
        Assert.Equal("4.6.3.stable.mono", entry.Name);
        Assert.Equal("4.6.3-stable-mono", entry.Release);
        Assert.Equal("mono", entry.Runtime);
    }

    [Fact]
    public void TemplateHelp_WritesGroupHelp()
    {
        var console = new TestConsole();
        var command = new TemplateHelpCommand(console);

        command.Show();

        Assert.Contains("Manage Godot export templates.", console.Output);
        Assert.Contains("Usage: fgvm template <COMMAND>", console.Output);
        Assert.Contains("install, i", console.Output);
        Assert.Contains("list, l", console.Output);
        Assert.Contains("remove, r", console.Output);
    }

    [Fact]
    public void ListAlias_ForwardsJsonFlag()
    {
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.List())
            .Returns(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]));

        var command = CreateCommand(orchestrator.Object, out var console);

        command.ListAlias(true);

        Assert.Equal("[]", console.Output.Trim());
        orchestrator.Verify(x => x.List(), Times.Once);
    }

    [Fact]
    public async Task Remove_ForwardsQuery()
    {
        var query = new[] { "4.6" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.RemoveAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<Unit, TemplateRegistryError>.Success(Unit.Value));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.Remove(CancellationToken.None, query);

        orchestrator.Verify(x => x.RemoveAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAlias_ForwardsQuery()
    {
        var query = new[] { "4.6" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.RemoveAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<Unit, TemplateRegistryError>.Success(Unit.Value));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.RemoveAlias(CancellationToken.None, query);

        orchestrator.Verify(x => x.RemoveAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TemplateCommand CreateCommand(ITemplateOrchestrator orchestrator, out TestConsole console)
    {
        console = new TestConsole();
        return new TemplateCommand(
            orchestrator,
            CreatePathServiceMock().Object,
            console,
            NullLogger<TemplateCommand>.Instance);
    }

    private static Mock<IPathService> CreatePathServiceMock()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-template-command-tests", Guid.NewGuid().ToString("N"));
        var mock = new Mock<IPathService>();
        mock.SetupGet(x => x.RootPath).Returns(rootPath);
        mock.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, "fgvm.log"));
        return mock;
    }
}
