using System.Runtime.InteropServices;
using System.Security;
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
    public async Task Install_WithTemplatesAndVerbose_ForwardsVerboseToBothInstallers()
    {
        var query = new[] { "4.6.2" };
        const string releaseName = "4.6.2-stable-standard";
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.NewInstallation(releaseName, new ChecksumVerification.Verified()),
            verbose: true);
        var templateOrchestrator = CreateSuccessfulTemplateOrchestrator(releaseName, verbose: true);
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await command.Install(withTemplates: true, verbose: true, cancellationToken: CancellationToken.None, query: query);

        templateOrchestrator.Verify(x =>
                x.InstallAsync(
                    It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                    false,
                    true,
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
                x.InstallAsync(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Install_WithTemplates_DoesNotInstallTemplatesWhenEditorInstallFails()
    {
        var query = new[] { "missing" };
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(new InstallationError.NotFound("missing")));
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query));

        templateOrchestrator.Verify(x =>
                x.InstallAsync(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
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
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                new TemplateInstallationError.Failed(
                    "Request failed with 403 (Forbidden). Response: API rate limit exceeded [shared IP]")));
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out var console);
        console.Profile.Width = 500;

        await command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query);

        Assert.Contains($"Godot {releaseName} is installed", console.Output);
        Assert.Contains("export template installation failed", console.Output);
        Assert.Contains("API rate limit exceeded [shared IP]", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenInstallationFails_DoesNotRenderMessageMarkupLiterally()
    {
        var query = new[] { "4.6.2" };
        var installationOrchestrator = CreateFailedInstallationOrchestrator(
            query,
            new InstallationError.Failed("Download from [mirror] failed"));
        var command = CreateCommand(
            installationOrchestrator.Object,
            new Mock<ITemplateOrchestrator>().Object,
            out var console);
        console.Profile.Width = 500;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command.Install(cancellationToken: CancellationToken.None, query: query));

        Assert.Equal("Installation failed: Download from [mirror] failed", error.Message);
        Assert.Contains("Installation failed: Download from [mirror] failed", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("[red]", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenVersionIsNotFound_ThrowsPlainTextArgumentException()
    {
        var query = new[] { "[9.999]" };
        var installationOrchestrator = CreateFailedInstallationOrchestrator(
            query,
            new InstallationError.NotFound(query[0]));
        var command = CreateCommand(
            installationOrchestrator.Object,
            new Mock<ITemplateOrchestrator>().Object,
            out _);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            command.Install(cancellationToken: CancellationToken.None, query: query));

        Assert.Equal("Version [9.999] could not be found for Linux x64", error.Message);
        Assert.DoesNotContain("[red]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenChecksumMismatches_DoesNotRenderMessageMarkupLiterally()
    {
        var query = new[] { "4.6.2" };
        var installationOrchestrator = CreateFailedInstallationOrchestrator(
            query,
            new InstallationError.ChecksumMismatch("expected", "actual", "Godot [fixture].zip"));
        var command = CreateCommand(
            installationOrchestrator.Object,
            new Mock<ITemplateOrchestrator>().Object,
            out var console);
        console.Profile.Width = 500;

        var error = await Assert.ThrowsAsync<SecurityException>(() =>
            command.Install(cancellationToken: CancellationToken.None, query: query));

        Assert.Contains("Checksum mismatch for Godot [fixture].zip!", error.Message, StringComparison.Ordinal);
        Assert.Contains("Expected: expected", error.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:   actual", error.Message, StringComparison.Ordinal);
        Assert.Contains("corrupted download or security issue", error.Message, StringComparison.Ordinal);
        Assert.Contains("Checksum mismatch for Godot [fixture].zip", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("[red]", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenEditorInstallationIsCanceled_ReportsCancellationAndSkipsTemplates()
    {
        var query = new[] { "4.6.2" };
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out var console);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query));

        Assert.Contains("User cancelled installation operation", console.Output);
        templateOrchestrator.Verify(x =>
                x.InstallAsync(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Install_WhenReleaseLookupFails_ShowsTheFailureDetails()
    {
        var query = new[] { "4.6" };
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Unable to fetch available Godot releases: Request to https://[::1] failed with 403 (Forbidden)"));
        var command = CreateCommand(
            installationOrchestrator.Object,
            new Mock<ITemplateOrchestrator>().Object,
            out var console);
        console.Profile.Width = 500;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            command.Install(cancellationToken: CancellationToken.None, query: query));

        Assert.Contains("Unable to fetch available Godot releases", console.Output, StringComparison.Ordinal);
        Assert.Contains("https://[::1]", console.Output, StringComparison.Ordinal);
        Assert.Contains("403 (Forbidden)", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_WhenTemplateInstallationIsCanceled_KeepsTheEditorAndSucceeds()
    {
        var query = new[] { "4.6.2" };
        const string releaseName = "4.6.2-stable-standard";
        var installationOrchestrator = CreateInstallationOrchestrator(query,
            new InstallationOutcome.NewInstallation(releaseName, new ChecksumVerification.Verified()));
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        templateOrchestrator.Setup(x => x.InstallAsync(
                It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var command = CreateCommand(installationOrchestrator.Object, templateOrchestrator.Object, out var console);

        await command.Install(withTemplates: true, cancellationToken: CancellationToken.None, query: query);

        Assert.Contains($"Godot {releaseName} is installed", console.Output);
        Assert.Contains("Export templates were skipped", console.Output);
        Assert.DoesNotContain("Export template installation failed", console.Output);
    }

    private static Mock<IInstallationOrchestrator> CreateInstallationOrchestrator(string[] query,
        InstallationOutcome outcome,
        bool verbose = false
    )
    {
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, verbose, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(outcome));
        return installationOrchestrator;
    }

    private static Mock<IInstallationOrchestrator> CreateFailedInstallationOrchestrator(string[] query, InstallationError error)
    {
        var installationOrchestrator = new Mock<IInstallationOrchestrator>();
        installationOrchestrator.Setup(x => x.InstallAsync(query, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(error));
        return installationOrchestrator;
    }

    private static Mock<ITemplateOrchestrator> CreateSuccessfulTemplateOrchestrator(string releaseName, bool verbose = false)
    {
        var templateOrchestrator = new Mock<ITemplateOrchestrator>();
        templateOrchestrator.Setup(x => x.InstallAsync(
                It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { releaseName })),
                false,
                verbose,
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
