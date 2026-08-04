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
    public async Task Install_ForwardsQueryForceAndVerboseFlags()
    {
        var query = new[] { "4.6", "mono" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.InstallAsync(query, true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled("4.6.stable.mono", "/templates/4.6.stable.mono")));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.Install(true, true, CancellationToken.None, query);

        orchestrator.Verify(x => x.InstallAsync(query, true, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAlias_ForwardsQueryForceAndVerboseFlags()
    {
        var query = new[] { "4.6", "mono" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.InstallAsync(query, true, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled("4.6.stable.mono", "/templates/4.6.stable.mono")));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.InstallAlias(true, true, CancellationToken.None, query);

        orchestrator.Verify(x => x.InstallAsync(query, true, true, It.IsAny<CancellationToken>()), Times.Once);
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
        orchestrator.Setup(x => x.SelectForRemovalAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.Remove(CancellationToken.None, query);

        orchestrator.Verify(x => x.SelectForRemovalAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAlias_ForwardsQuery()
    {
        var query = new[] { "4.6" };
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.SelectForRemovalAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]));

        var command = CreateCommand(orchestrator.Object, out _);

        await command.RemoveAlias(CancellationToken.None, query);

        orchestrator.Verify(x => x.SelectForRemovalAsync(query, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Remove_UsesDurableRemovalForSelectedTemplate()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-template-command-tests", Guid.NewGuid().ToString("N"));
        var templatePath = Path.Combine(rootPath, "templates", "4.6.stable");
        Directory.CreateDirectory(templatePath);
        var installation = new TemplateInstallation(
            "4.6.stable",
            "4.6-stable-standard",
            RuntimeEnvironment.Standard,
            templatePath,
            null);
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.SelectForRemovalAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([installation]));
        var command = CreateCommand(orchestrator.Object, out var console, rootPath);

        try
        {
            await command.Remove(query: ["4.6"]);

            Assert.False(Directory.Exists(templatePath));
            Assert.Contains("Successfully removed export templates", console.Output);
            Assert.Empty(Directory.GetDirectories(rootPath, ".fgvm-removing-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_WhenCancelledDuringSelection_LeavesSelectedTemplateInPlace()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-template-command-tests", Guid.NewGuid().ToString("N"));
        var templatePath = Path.Combine(rootPath, "templates", "4.6.stable");
        Directory.CreateDirectory(templatePath);
        var orchestrator = new Mock<ITemplateOrchestrator>();

        // A cancelled prompt throws from selection; it doesn't just flip the token
        orchestrator.Setup(x => x.SelectForRemovalAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var command = CreateCommand(orchestrator.Object, out _, rootPath);

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                command.Remove(CancellationToken.None, "4.6"));

            Assert.True(Directory.Exists(templatePath));
            Assert.Empty(Directory.GetDirectories(rootPath, ".fgvm-removing-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Remove_WhenCancelledAfterSelection_StillRemovesTheTemplate()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-template-command-tests", Guid.NewGuid().ToString("N"));
        var templatePath = Path.Combine(rootPath, "templates", "4.6.stable");
        Directory.CreateDirectory(templatePath);
        var installation = new TemplateInstallation(
            "4.6.stable",
            "4.6-stable-standard",
            RuntimeEnvironment.Standard,
            templatePath,
            null);
        var cancellation = new CancellationTokenSource();
        var orchestrator = new Mock<ITemplateOrchestrator>();
        orchestrator.Setup(x => x.SelectForRemovalAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                cancellation.Cancel();
                return new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([installation]);
            });
        var command = CreateCommand(orchestrator.Object, out _, rootPath);

        try
        {
            await command.Remove(cancellation.Token, "4.6");

            Assert.False(Directory.Exists(templatePath));
            Assert.Empty(Directory.GetDirectories(rootPath, ".fgvm-removing-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static TemplateCommand CreateCommand(ITemplateOrchestrator orchestrator,
        out TestConsole console,
        string? rootPath = null
    )
    {
        console = new TestConsole();
        var pathService = CreatePathServiceMock(rootPath).Object;
        var godotPathService = new Mock<IGodotPathService>();
        godotPathService.Setup(x => x.ExportTemplatesRootPath).Returns(Path.Combine(pathService.RootPath, "templates"));
        return new TemplateCommand(
            orchestrator,
            CreateRemovalService(pathService, godotPathService.Object, console),
            pathService,
            console,
            NullLogger<TemplateCommand>.Instance);
    }

    private static RemovalService CreateRemovalService(IPathService pathService,
        IGodotPathService godotPathService,
        TestConsole console
    )
    {
        var hostSystem = new HostSystem(new SystemInfo(), pathService, NullLogger<HostSystem>.Instance);
        return new RemovalService(
            new DirectoryRemoval(hostSystem, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System),
            hostSystem,
            pathService,
            godotPathService,
            console,
            NullLogger<RemovalService>.Instance);
    }

    private static Mock<IPathService> CreatePathServiceMock(string? rootPath = null)
    {
        rootPath ??= Path.Combine(Path.GetTempPath(), "fgvm-template-command-tests", Guid.NewGuid().ToString("N"));
        var mock = new Mock<IPathService>();
        mock.SetupGet(x => x.RootPath).Returns(rootPath);
        mock.SetupGet(x => x.InstallationsPath).Returns(Path.Combine(rootPath, "installations.json"));
        mock.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, "fgvm.log"));
        return mock;
    }
}
