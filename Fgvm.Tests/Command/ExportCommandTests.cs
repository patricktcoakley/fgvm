using System.Runtime.InteropServices;
using System.Text.Json;
using Fgvm.Cli;
using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class ExportCommandTests
{
    private readonly Mock<IExportPresetCatalog> _exportPresetCatalog = new();
    private readonly Mock<IGodotLauncher> _godotLauncher = new();
    private readonly Mock<IHostSystem> _hostSystem = new();
    private readonly Mock<IDirectoryRemoval> _directoryRemoval = new();
    private readonly List<IReadOnlyList<string>> _invocations = [];
    private readonly Mock<ITemplateOrchestrator> _templateOrchestrator = new();
    private readonly Mock<IVersionManagementService> _versionManagementService = new();

    public ExportCommandTests()
    {
        SetupLocalRelease(CreateRelease("4.6.2-stable-standard"));
        SetupTemplateSuccess();
        SetupResolvedEditor("4.6.2-stable-standard");
        SetupFilesystemSuccess();
        SetupGodotExit(0);
    }

    [Fact]
    public async Task Export_ImportsOnceBeforeExportingEveryPresetInOrder()
    {
        SetupPresets([
            Preset(0, "Linux", "Linux", "build/linux/game"),
            Preset(1, "Web Demo", "Web", "build/web/index.html")
        ]);

        await CreateCommand(out _, out _).Export();

        Assert.Equal(3, _invocations.Count);
        Assert.Contains("--import", _invocations[0]);
        Assert.Equal(["Linux", "Web Demo"], _invocations.Skip(1).Select(PresetOf));
    }

    [Fact]
    public async Task Export_PassesPresetNamesAsSingleArguments()
    {
        SetupPresets([Preset(0, "Windows Demo", "Windows Desktop", "build/game.exe")]);

        await CreateCommand(out _, out _).Export();

        Assert.Equal("Windows Demo", PresetOf(_invocations[1]));
    }

    [Fact]
    public async Task Export_WithConfiguredAndroidPreset_LetsGodotHandleThePlatform()
    {
        SetupPresets([Preset(0, "Android", "Android", "build/game.apk")]);

        await CreateCommand(out _, out _).Export();

        Assert.Equal("Android", PresetOf(_invocations[1]));
    }

    [Fact]
    public async Task Export_WithoutAProject_FailsBeforeChangingState()
    {
        _hostSystem.Setup(system => system.FileExists(It.Is<string>(path =>
                path.EndsWith("project.godot", StringComparison.Ordinal))))
            .Returns(new Result<bool, FileOperationError>.Success(false));

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => CreateCommand(out _, out _).Export());

        Assert.Contains("project.godot", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WhenProjectInspectionFails_PreservesTheRealFailure()
    {
        _hostSystem.Setup(system => system.FileExists(It.Is<string>(path =>
                path.EndsWith("project.godot", StringComparison.Ordinal))))
            .Returns(new Result<bool, FileOperationError>.Failure(
                new FileOperationError.PermissionDenied("/project/project.godot")));

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => CreateCommand(out _, out _).Export());

        Assert.Contains("Permission denied", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WithUnknownPlatformAndNoPath_FailsBeforeChangingState()
    {
        SetupPresets([Preset(0, "Console", "Custom Console", null)]);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => CreateCommand(out _, out _).Export());

        Assert.Contains("export_path", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WithMalformedPresetFile_FailsBeforeChangingState()
    {
        _exportPresetCatalog.Setup(catalog => catalog.Read(null))
            .Returns(new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                new ExportPresetCatalogError.InvalidConfiguration(
                    "/project/export_presets.cfg", 4, "Invalid runnable value.")));

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() => CreateCommand(out _, out _).Export());

        Assert.Contains("line 4", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WithManifestCollidingWithAFileArtifact_FailsBeforeChangingState()
    {
        SetupPresets([Preset(0, "Web", "Web", "build/game.zip")]);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() =>
            CreateCommand(out _, out _).Export(manifest: "build/game.zip"));

        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WhenTemplatesCannotBeInstalled_DoesNotRunGodot()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/game")]);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                new TemplateInstallationError.NotFound("4.6.2-stable-standard")));

        await Assert.ThrowsAsync<ProcessExitCodeException>(() => CreateCommand(out _, out _).Export());

        Assert.Empty(_invocations);
    }

    [Fact]
    public async Task Export_WhenGodotReportsSuccessWithoutAnArtifact_Fails()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/game")]);
        _hostSystem.Setup(system => system.FileExists(It.Is<string>(path =>
                path.Contains(".fgvm-export-staging-", StringComparison.Ordinal))))
            .Returns(new Result<bool, FileOperationError>.Success(false));

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => CreateCommand(out _, out _).Export());

        Assert.Equal(ExitCodes.GeneralError, exception.ExitCode);
    }

    [Fact]
    public async Task Export_WhenALaterTargetFails_DoesNotPromoteEarlierTargetsOrWriteAManifest()
    {
        SetupPresets([
            Preset(0, "Linux", "Linux", "build/linux/game"),
            Preset(1, "Web", "Web", "build/web/index.html")
        ]);
        var invocation = 0;
        _godotLauncher.Setup(launcher => launcher.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(), It.IsAny<Action<GodotLaunchOutput>?>(), It.IsAny<CancellationToken>()))
            .Returns((GodotLaunchRequest request, Action<GodotLaunchOutput>? _, CancellationToken __) =>
            {
                _invocations.Add(request.Arguments);
                var exitCode = invocation++ == 2 ? 42 : 0;
                return Task.FromResult<Result<GodotLaunchOutcome, GodotLaunchError>>(
                    new Result<GodotLaunchOutcome, GodotLaunchError>.Success(
                        new GodotLaunchOutcome.Exited(exitCode)));
            });

        await Assert.ThrowsAsync<ProcessExitCodeException>(() => CreateCommand(out _, out _).Export());

        _hostSystem.Verify(system => system.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _hostSystem.Verify(system => system.MoveFile(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _hostSystem.Verify(system => system.WriteAllText(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Export_AlwaysCommitsTheDefaultManifest()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/linux/game")]);

        await CreateCommand(out _, out _).Export();

        _hostSystem.Verify(system => system.MoveFile(
            It.Is<string>(path => path.Contains(".fgvm-export-staging-", StringComparison.Ordinal)),
            It.Is<string>(path => path.EndsWith(".fgvm-export.json", StringComparison.Ordinal)),
            false), Times.Once);
    }

    [Fact]
    public async Task Export_InvalidatesTheDefaultManifestBeforePreparationFails()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/linux/game")]);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(
                new TemplateInstallationError.NotFound("4.6.2-stable-standard")));

        await Assert.ThrowsAsync<ProcessExitCodeException>(() => CreateCommand(out _, out _).Export());

        _hostSystem.Verify(system => system.DeleteFileIfExists(
            It.Is<string>(path => path.EndsWith(".fgvm-export.json", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task Export_RejectsAManifestNestedInsideAnArtifactBeforeChangingState()
    {
        SetupPresets([Preset(0, "Web", "Web", "")]);

        var exception = await Assert.ThrowsAsync<ConfigurationException>(() =>
            CreateCommand(out _, out _).Export(manifest: "dist/web/manifest.json"));

        Assert.Contains("conflicts", exception.Message, StringComparison.Ordinal);
        VerifyNothingWasPrepared();
    }

    [Fact]
    public async Task Export_WithArchive_UsesGodotsNativeZipPackExport()
    {
        SetupPresets([Preset(0, "Windows Demo", "Windows Desktop", "build/game.exe")]);

        await CreateCommand(out _, out _).Export(archive: true);

        var exportArguments = _invocations[1];
        Assert.Contains("--export-pack", exportArguments);
        Assert.DoesNotContain("--export-release", exportArguments);
        Assert.EndsWith(".zip", exportArguments[^1], StringComparison.OrdinalIgnoreCase);
        _hostSystem.Verify(system => system.MoveFile(
            It.IsAny<string>(), It.Is<string>(path => path.EndsWith("game.zip", StringComparison.Ordinal)), false), Times.Once);
    }

    [Fact]
    public async Task Export_WithOutputRoot_CommitsAnIsolatedStagedDirectory()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/game")]);

        await CreateCommand(out _, out _).Export(output: "artifacts");

        _hostSystem.Verify(system => system.MoveDirectory(
            It.Is<string>(path => path.Contains(".fgvm-export-staging-", StringComparison.Ordinal)),
            It.Is<string>(path => path.Replace('\\', '/').EndsWith("artifacts/linux", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task Export_WithIsolatedMacApp_StagesAndCommitsTheAppBundle()
    {
        SetupPresets([Preset(0, "macOS", "macOS", "")]);
        _hostSystem.Setup(system => system.DirectoryExists(It.Is<string>(path =>
                path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))))
            .Returns(new Result<bool, FileOperationError>.Success(true));

        await CreateCommand(out _, out _).Export(output: "artifacts");

        _hostSystem.Verify(system => system.MoveDirectory(
                It.Is<string>(path => path.Contains(".fgvm-export-staging-", StringComparison.Ordinal) &&
                                      path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)),
                It.Is<string>(path => path.Replace('\\', '/').EndsWith("artifacts/macos/macos.app", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task Export_AllowsDistinctConfiguredFilesInOneDirectory()
    {
        SetupPresets([
            Preset(0, "Windows", "Windows Desktop", "build/game.exe"),
            Preset(1, "Linux", "Linux", "build/game.x86_64")
        ]);

        await CreateCommand(out _, out _).Export();

        Assert.Equal(["Windows", "Linux"], _invocations.Skip(1).Select(PresetOf));
    }

    [Fact]
    public async Task Export_WritesAStronglyTypedManifestUsingTheActualLaunchVersion()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/linux/game")]);
        SetupResolvedEditor("4.6.3-stable-standard");
        string? written = null;
        _hostSystem.Setup(system => system.WriteAllText(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, contents) => written = contents)
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));

        await CreateCommand(out _, out _).Export(manifest: "dist/manifest.json");

        var manifest = JsonSerializer.Deserialize<ExportManifestView>(written!, JsonView.Options);
        Assert.Equal(ExportManifestView.CurrentVersion, manifest.ManifestVersion);
        Assert.NotEqual(Guid.Empty, manifest.RunId);
        Assert.Equal("4.6.3-stable-standard", manifest.Godot);
        var target = Assert.Single(manifest.Targets);
        Assert.Equal("Linux", target.Preset);
        Assert.Equal("release", target.Mode);
        Assert.Equal("directory", target.Kind);
        Assert.Equal("build/linux", target.Path);
    }

    [Fact]
    public async Task Export_JsonWritesOnlyTheManifestToStandardOutput()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/game")]);

        await CreateCommand(out var console, out var output).Export(json: true);

        var manifest = JsonSerializer.Deserialize<ExportManifestView>(output.ToString(), JsonView.Options);
        Assert.Single(manifest.Targets);
        Assert.DoesNotContain("Exported", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Exporting", console.Output, StringComparison.Ordinal);
        _versionManagementService.Verify(service => service.PrepareLocalVersionAsync(
            It.IsAny<string[]>(),
            It.IsAny<bool>(),
            It.IsNotNull<IAnsiConsole>(),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Export_JsonWritesUnavailableTemplateChecksumWarningOnlyToDiagnostics()
    {
        SetupPresets([Preset(0, "Linux", "Linux", "build/game")]);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<Release>(),
                It.IsAny<IOperationProgress<TemplateInstallationStage>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.NewInstallation(
                    "4.6.2.stable",
                    "/templates/4.6.2.stable",
                    new ChecksumVerification.Unavailable())));

        await CreateCommand(out var console, out var diagnostics, out var output).Export(json: true);

        var manifest = JsonSerializer.Deserialize<ExportManifestView>(output.ToString(), JsonView.Options);
        Assert.Single(manifest.Targets);
        Assert.Empty(console.Output);
        Assert.Contains("Checksum unavailable", diagnostics.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticBuffer_RetainsOnlyItsHeadAndTail()
    {
        var diagnostics = new ExportRunner.DiagnosticBuffer(10, 10);

        for (var index = 0; index < 1_000; index++)
        {
            diagnostics.Add($"line-{index}");
        }

        Assert.Equal(1_000, diagnostics.Count);
        Assert.Equal(980, diagnostics.OmittedCount);
        Assert.Equal(20, diagnostics.Lines.Count());
        Assert.Equal("line-0", diagnostics.Head[0]);
        Assert.Equal("line-9", diagnostics.Head[^1]);
        Assert.Equal("line-990", diagnostics.Tail.First());
        Assert.Equal("line-999", diagnostics.Tail.Last());
    }

    private void VerifyNothingWasPrepared()
    {
        _versionManagementService.Verify(service => service.PrepareLocalVersionAsync(
            It.IsAny<string[]>(),
            It.IsAny<bool>(),
            It.IsAny<IAnsiConsole?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_invocations);
    }

    private static string? PresetOf(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("--export-release");
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }

    private static ExportPreset Preset(int index, string name, string platform, string? exportPath) =>
        new(index, name, platform, exportPath, null);

    private ExportCommand CreateCommand(out TestConsole console, out StringWriter standardOutput)
    {
        return CreateCommand(out console, out _, out standardOutput);
    }

    private ExportCommand CreateCommand(out TestConsole console,
        out TestConsole diagnosticConsole,
        out StringWriter standardOutput
    )
    {
        console = new TestConsole();
        console.Profile.Width = 200;
        diagnosticConsole = new TestConsole();
        diagnosticConsole.Profile.Width = 200;
        standardOutput = new StringWriter();
        var exportRunner = new ExportRunner(
            _godotLauncher.Object,
            _hostSystem.Object,
            _directoryRemoval.Object,
            NullLogger<ExportRunner>.Instance);
        return new ExportCommand(
            _versionManagementService.Object,
            _exportPresetCatalog.Object,
            _templateOrchestrator.Object,
            exportRunner,
            _hostSystem.Object,
            standardOutput,
            console,
            new DiagnosticConsole(diagnosticConsole),
            NullLogger<ExportCommand>.Instance);
    }

    private void SetupPresets(IReadOnlyList<ExportPreset> presets) =>
        _exportPresetCatalog.Setup(catalog => catalog.Read(null))
            .Returns(new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(presets));

    private void SetupLocalRelease(Release release) =>
        _versionManagementService.Setup(service => service.PrepareLocalVersionAsync(
                It.IsAny<string[]>(),
                It.IsAny<bool>(),
                It.IsAny<IAnsiConsole?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(release);

    private void SetupTemplateSuccess()
    {
        var result = new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
            new TemplateInstallationOutcome.AlreadyInstalled("4.6.2.stable", "/templates/4.6.2.stable"));
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<string[]>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        _templateOrchestrator.Setup(orchestrator => orchestrator.InstallAsync(
                It.IsAny<Release>(),
                It.IsAny<IOperationProgress<TemplateInstallationStage>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private void SetupResolvedEditor(string version) =>
        _versionManagementService.Setup(service => service.ResolveInstalledVersionAsync(
                It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(
                new VersionResolutionOutcome.Found("/godot/godot", "/godot", version, true, version)));

    private void SetupFilesystemSuccess()
    {
        _hostSystem.SetupGet(system => system.SystemInfo)
            .Returns(new SystemInfo(OS.Linux, Architecture.Arm64));
        _hostSystem.Setup(system => system.CreateDirectory(It.IsAny<string>()))
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));
        _hostSystem.Setup(system => system.DeleteDirectoryIfExists(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));
        _hostSystem.Setup(system => system.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));
        _hostSystem.Setup(system => system.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));
        _hostSystem.Setup(system => system.FileExists(It.IsAny<string>()))
            .Returns((string path) => new Result<bool, FileOperationError>.Success(
                path.EndsWith("project.godot", StringComparison.Ordinal) ||
                path.Contains(".fgvm-export-staging-", StringComparison.Ordinal)));
        _hostSystem.Setup(system => system.DirectoryExists(It.IsAny<string>()))
            .Returns(new Result<bool, FileOperationError>.Success(false));
        _hostSystem.Setup(system => system.WriteAllText(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Result<Unit, FileOperationError>.Success(Unit.Value));
        _hostSystem.Setup(system => system.EnumerateEntries(It.IsAny<string>()))
            .Returns((string path) => new Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(
                path.Contains(".fgvm-export-staging-", StringComparison.Ordinal)
                    ? [new HostDirectoryEntry(Path.Combine(path, "game"), "game", FileAttributes.Normal, DateTimeOffset.UtcNow)]
                    : []));
    }

    private void SetupGodotExit(int exitCode) =>
        _godotLauncher.Setup(launcher => launcher.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(), It.IsAny<Action<GodotLaunchOutput>?>(), It.IsAny<CancellationToken>()))
            .Returns((GodotLaunchRequest request, Action<GodotLaunchOutput>? _, CancellationToken __) =>
            {
                _invocations.Add(request.Arguments);
                return Task.FromResult<Result<GodotLaunchOutcome, GodotLaunchError>>(
                    new Result<GodotLaunchOutcome, GodotLaunchError>.Success(new GodotLaunchOutcome.Exited(exitCode)));
            });

    private static Release CreateRelease(string releaseNameWithRuntime)
    {
        var release = Release.TryParse(releaseNameWithRuntime);
        Assert.NotNull(release);
        return release;
    }
}
