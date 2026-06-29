using System.Runtime.InteropServices;
using Fgvm.Cli.Command;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class InstallCommandTests
{
    [Fact]
    public async Task Install_WithTemplates_InstallsTemplatesForNewInstallation()
    {
        var query = new[] { "4.6.2" };
        const string releaseName = "4.6.2-stable-standard";
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.NewInstallation(releaseName, new ChecksumVerification.Verified()));
        var templateOrchestrator = CreateSuccessfulTemplateOrchestrator(releaseName);
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query);

        templateOrchestrator.Verify(x =>
                x.InstallAsync(
                    It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                    false,
                    It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Install_WithTemplates_InstallsTemplatesForAlreadyInstalledVersion()
    {
        var query = new[] { "4.6.2" };
        const string releaseName = "4.6.2-stable-standard";
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.AlreadyInstalled(releaseName));
        var templateOrchestrator = CreateSuccessfulTemplateOrchestrator(releaseName);
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query);

        templateOrchestrator.Verify(x =>
                x.InstallAsync(
                    It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                    false,
                    It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Install_WithoutTemplates_DoesNotInstallTemplates()
    {
        var query = new[] { "4.6.2" };
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.NewInstallation("4.6.2-stable-standard", new ChecksumVerification.Verified()));
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await command.Install(cancellationToken: CancellationToken.None, query: query);

        templateOrchestrator.Verify(x =>
                x.InstallAsync(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Install_WithTemplates_DoesNotInstallTemplatesWhenEditorInstallFails()
    {
        var query = new[] { "missing" };
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(new InstallationError.NotFound("missing")));
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query));

        templateOrchestrator.Verify(x =>
                x.InstallAsync(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Install_WithTemplates_WarnsWhenTemplateInstallFails()
    {
        var query = new[] { "4.6.2" };
        const string releaseName = "4.6.2-stable-standard";
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.NewInstallation(releaseName, new ChecksumVerification.Verified()));
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        templateOrchestrator.Setup(x => x.InstallAsync(
                It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed("Download failed for export templates.")));
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out var console);

        await command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query);

        Assert.Contains($"Godot {releaseName} is installed", console.Output);
        Assert.Contains("Export template installation failed", console.Output);
    }

    private static Mock<IInstallationOrchestrator> CreateInstallationOrchestrator(string[] query,
        InstallationOutcome outcome
    )
    {
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(outcome));
        return installationOrchestrator;
    }

    private static Mock<ITemplateOrchestrator> CreateSuccessfulTemplateOrchestrator(string releaseName)
    {
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        templateOrchestrator.Setup(x => x.InstallAsync(
                It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled("4.6.2.stable", "/templates/4.6.2.stable")));
        return templateOrchestrator;
    }

    private static InstallCommand CreateCommand(IInstallationOrchestrator installationOrchestrator,
        ITemplateOrchestrator templateOrchestrator,
        out TestConsole console
    )
    {
        console = new TestConsole();
        return new InstallCommand(
            CreateHostSystemMock().Object,
            installationOrchestrator,
            templateOrchestrator,
            CreatePathServiceMock().Object,
            console,
            NullLogger<InstallCommand>.Instance);
    }

    private static Mock<IHostSystem> CreateHostSystemMock()
    {
        var hostSystem = new Mock<IHostSystem>();
        hostSystem.SetupGet(x => x.SystemInfo).Returns(new SystemInfo(OS.Linux, Architecture.X64));
        return hostSystem;
    }

    private static Mock<IPathService> CreatePathServiceMock()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-install-command-tests", Guid.NewGuid().ToString("N"));
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(rootPath);
        pathService.SetupGet(x => x.LogPath).Returns(Path.Combine(rootPath, "fgvm.log"));
        return pathService;
    }
}
