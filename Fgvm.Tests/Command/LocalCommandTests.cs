using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class LocalCommandTests
{
    private readonly Mock<IExportPresetCatalog> _exportPresetCatalog = new();
    private readonly Mock<ITemplateOrchestrator> _templateOrchestrator = new();
    private readonly Mock<IVersionManagementService> _versionManagementService = new();

    public static TheoryData<TemplateInstallationError, string> TemplateFailures =>
        new()
        {
            { new TemplateInstallationError.InvalidQuery("invalid [query]"), "invalid [query]" },
            { new TemplateInstallationError.NotFound("4.6.2-stable-standard"), "could not be found" },
            {
                new TemplateInstallationError.ChecksumMismatch("expected", "actual", "templates [fixture].tpz"),
                "Checksum mismatch for templates [fixture].tpz"
            },
            { new TemplateInstallationError.Failed("download [unavailable]"), "download [unavailable]" }
        };

    [Fact]
    public async Task Local_WithConfiguredPreset_InstallsTemplatesForSelectedRelease()
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([
            new ExportPreset(0, "Web", "Web", "build/web/index.html", false)
        ]);
        SetupLocalRelease(release);
        SetupTemplateSuccess("4.6.2.stable");
        var command = CreateCommand(out var console);

        await command.Local();

        _templateOrchestrator.Verify(orchestrator => orchestrator.InstallAsync(
            It.Is<string[]>(query => query.SequenceEqual(new[] { release.ReleaseNameWithRuntime })),
            false,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("local version", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Local_WithNoPresetFile_SkipsTemplateInstallation()
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([]);
        SetupLocalRelease(release);
        var command = CreateCommand(out _);

        await command.Local();

        _templateOrchestrator.Verify(orchestrator => orchestrator.InstallAsync(
            It.IsAny<string[]>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Local_WithOnlyIncompletePresets_SkipsTemplateInstallation()
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([
            new ExportPreset(0, "Web", "Web", "", true)
        ]);
        SetupLocalRelease(release);
        var command = CreateCommand(out _);

        await command.Local();

        _templateOrchestrator.Verify(orchestrator => orchestrator.InstallAsync(
            It.IsAny<string[]>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Local_WhenPresetCatalogFails_DoesNotChangeLocalVersion()
    {
        _exportPresetCatalog.Setup(catalog => catalog.Read(null))
            .Returns(new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                new ExportPresetCatalogError.InvalidConfiguration("/project/export_presets.cfg", 7, "Invalid runnable value.")));
        var command = CreateCommand(out _);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => command.Local());

        Assert.Contains("export_presets.cfg", exception.Message);
        Assert.Contains("line 7", exception.Message);
        _versionManagementService.Verify(service => service.SetLocalVersionAsync(
            It.IsAny<string[]?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(TemplateFailures))]
    public async Task Local_WhenTemplateInstallationFails_ReportsReasonAfterSettingVersion(TemplateInstallationError templateError,
        string expectedReason
    )
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([
            new ExportPreset(0, "Linux", "Linux/X11", "build/linux/game.x86_64", true)
        ]);
        SetupLocalRelease(release);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(templateError));
        var command = CreateCommand(out var console);

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => command.Local());

        Assert.Equal(ExitCodes.GeneralError, exception.ExitCode);
        Assert.Contains("Set local version to 4.6.2-stable-standard", console.Output);
        Assert.Contains(expectedReason, console.Output);
        Assert.DoesNotContain("Something went wrong", console.Output);
        _versionManagementService.Verify(service => service.SetLocalVersionAsync(
            null,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Local_WhenTemplateOrchestratorThrows_ReportsRetainedLocalVersionAndCause()
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([
            new ExportPreset(0, "Web", "Web", "build/web/index.html", true)
        ]);
        SetupLocalRelease(release);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk [full]"));
        var command = CreateCommand(out var console);

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => command.Local());

        Assert.Equal(ExitCodes.GeneralError, exception.ExitCode);
        Assert.Contains("Set local version to 4.6.2-stable-standard", console.Output);
        Assert.Contains("disk [full]", console.Output);
        Assert.DoesNotContain("Something went wrong", console.Output);
        _versionManagementService.Verify(service => service.SetLocalVersionAsync(
            null,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Local_WhenPresetFileCannotBeRead_DoesNotChangeLocalVersion()
    {
        var fileError = new FileOperationError.PermissionDenied("/project/export_presets.cfg");
        _exportPresetCatalog.Setup(catalog => catalog.Read(null))
            .Returns(new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                new ExportPresetCatalogError.FileAccess(fileError)));
        var command = CreateCommand(out _);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => command.Local());

        Assert.Contains("Permission denied", exception.Message);
        Assert.Contains("export_presets.cfg", exception.Message);
        _versionManagementService.Verify(service => service.SetLocalVersionAsync(
            It.IsAny<string[]?>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _templateOrchestrator.Verify(orchestrator => orchestrator.InstallAsync(
            It.IsAny<string[]>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Local_WhenTemplateInstallationIsCancelled_ReportsRetainedLocalVersion()
    {
        var release = CreateRelease("4.6.2-stable-standard");
        SetupPresets([
            new ExportPreset(0, "Web", "Web", "build/web/index.html", true)
        ]);
        SetupLocalRelease(release);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var command = CreateCommand(out var console);

        await Assert.ThrowsAsync<OperationCanceledException>(() => command.Local());

        Assert.Contains("Set local version to 4.6.2-stable-standard", console.Output);
        Assert.Contains("was cancelled", console.Output);
        Assert.DoesNotContain("User cancelled setting local version", console.Output);
    }

    private LocalCommand CreateCommand(out TestConsole console)
    {
        console = new TestConsole();
        console.Profile.Width = 200;
        return new LocalCommand(
            _versionManagementService.Object,
            _exportPresetCatalog.Object,
            _templateOrchestrator.Object,
            console,
            NullLogger<LocalCommand>.Instance);
    }

    private void SetupPresets(IReadOnlyList<ExportPreset> presets) =>
        _exportPresetCatalog.Setup(catalog => catalog.Read(null))
            .Returns(new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(presets));

    private void SetupLocalRelease(Release release) =>
        _versionManagementService.Setup(service => service.SetLocalVersionAsync(
                null,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(release);

    private void SetupTemplateSuccess(string templateVersion) =>
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.AlreadyInstalled(templateVersion, $"/templates/{templateVersion}")));

    private static Release CreateRelease(string releaseNameWithRuntime)
    {
        var release = Release.TryParse(releaseNameWithRuntime);
        Assert.NotNull(release);
        return release;
    }
}
