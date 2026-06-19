using Fgvm.Cli.Command;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public sealed class GodotCommandTests
{
    private const string InstallationKey = "4.6-stable-standard@linux.x86_64";
    private readonly Mock<IGodotArgumentService> _argumentService = new();
    private readonly TestConsole _console = new();
    private readonly Mock<IGodotLauncher> _launcher = new();
    private readonly Mock<IInstallationRegistry> _registry = new();

    private readonly VersionResolutionOutcome.Found _resolution = new(
        "/fgvm/installations/4.6-stable-standard/linux.x86_64/Godot",
        "/fgvm/installations/4.6-stable-standard/linux.x86_64",
        "4.6-stable-standard",
        false,
        InstallationKey);

    private readonly Mock<IVersionManagementService> _versionService = new();

    public GodotCommandTests()
    {
        _versionService.Setup(x => x.ResolveVersionForLaunchExplicitAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<VersionResolutionOutcome, VersionResolutionError>.Success(_resolution));
        _versionService.SetupGet(x => x.HostSystem).Returns(new Mock<IHostSystem>().Object);

        _argumentService.Setup(x => x.ShouldForceAttachedMode(It.IsAny<string>())).Returns(false);
        _registry.Setup(x => x.RecordLaunch(It.IsAny<string>(), It.IsAny<DateTimeOffset?>()))
            .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));
    }

    [Fact]
    public async Task Launch_Detached_PassesResolvedTargetAndRecordsLaunch()
    {
        GodotLaunchRequest? captured = null;
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<GodotLaunchRequest, Action<GodotLaunchOutput>?, CancellationToken>((request, _, _) => captured = request)
            .ReturnsAsync(new Result<GodotLaunchOutcome, GodotLaunchError>.Success(new GodotLaunchOutcome.Detached(1234)));

        await CreateCommand().Launch(args: "--windowed");

        Assert.NotNull(captured);
        Assert.Equal(GodotLaunchMode.Detached, captured.Mode);
        Assert.Equal("--windowed", captured.Arguments);
        Assert.Equal(_resolution.ExecutablePath, captured.Target.ExecutablePath);
        Assert.Equal(_resolution.WorkingDirectory, captured.Target.WorkingDirectory);
        Assert.Equal(InstallationKey, captured.Target.InstallationKey);
        Assert.Contains("1234", _console.Output);
        _registry.Verify(x => x.RecordLaunch(InstallationKey, null), Times.Once);
    }

    [Fact]
    public async Task Launch_ForcedAttached_ForwardsOutputAndRecordsLaunch()
    {
        _argumentService.Setup(x => x.ShouldForceAttachedMode("--version")).Returns(true);
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((GodotLaunchRequest request, Action<GodotLaunchOutput>? onOutput, CancellationToken _) =>
            {
                Assert.Equal(GodotLaunchMode.Attached, request.Mode);
                onOutput?.Invoke(new GodotLaunchOutput.StandardOutput("Godot 4.6"));
                return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(new GodotLaunchOutcome.Exited(0));
            });

        await CreateCommand().Launch(args: "--version");

        Assert.Contains("Godot 4.6", _console.Output);
        Assert.Contains("attached mode", _console.Output, StringComparison.OrdinalIgnoreCase);
        _registry.Verify(x => x.RecordLaunch(InstallationKey, null), Times.Once);
    }

    [Fact]
    public async Task Launch_ProjectFlag_PrependsDetectedProjectPathToExplicitArguments()
    {
        var projectFilePath = Path.Combine(Path.GetTempPath(), "fgvm-project", "project.godot");
        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;
        GodotLaunchRequest? captured = null;
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<GodotLaunchRequest, Action<GodotLaunchOutput>?, CancellationToken>((request, _, _) => captured = request)
            .ReturnsAsync(new Result<GodotLaunchOutcome, GodotLaunchError>.Success(new GodotLaunchOutcome.Exited(0)));

        await CreateCommand(projectFilePath).Launch(attached: true, project: true, args: "--dump-extension-api --quit");

        Assert.NotNull(captured);
        Assert.Equal($"--path \"{projectDirectory}\" --dump-extension-api --quit", captured.Arguments);
        Assert.Contains("Auto-detected project file", _console.Output);
        _registry.Verify(x => x.RecordLaunch(InstallationKey, null), Times.Once);
    }

    [Fact]
    public async Task Launch_NonZeroExit_PropagatesGodotExitCode()
    {
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((GodotLaunchRequest _, Action<GodotLaunchOutput>? onOutput, CancellationToken _) =>
            {
                onOutput?.Invoke(new GodotLaunchOutput.StandardError("failure"));
                return new Result<GodotLaunchOutcome, GodotLaunchError>.Success(new GodotLaunchOutcome.Exited(42));
            });

        var exception = await Assert.ThrowsAsync<ProcessExitCodeException>(() => CreateCommand().Launch(attached: true));

        Assert.Equal(42, exception.ExitCode);
        Assert.Contains("failure", _console.Output);
        _registry.Verify(x => x.RecordLaunch(InstallationKey, null), Times.Once);
    }

    [Fact]
    public async Task Launch_StartFailure_DoesNotRecordLaunch()
    {
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<GodotLaunchOutcome, GodotLaunchError>.Failure(
                new GodotLaunchError.StartFailed(_resolution.ExecutablePath, "missing")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCommand().Launch());

        _registry.Verify(x => x.RecordLaunch(It.IsAny<string>(), It.IsAny<DateTimeOffset?>()), Times.Never);
    }

    [Fact]
    public async Task Launch_Cancellation_DoesNotRecordLaunch()
    {
        _launcher.Setup(x => x.LaunchAsync(
                It.IsAny<GodotLaunchRequest>(),
                It.IsAny<Action<GodotLaunchOutput>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateCommand().Launch());

        Assert.Contains("cancelled", _console.Output, StringComparison.OrdinalIgnoreCase);
        _registry.Verify(x => x.RecordLaunch(It.IsAny<string>(), It.IsAny<DateTimeOffset?>()), Times.Never);
    }

    private GodotCommand CreateCommand(string? projectFilePath = null)
    {
        var projectManager = new Mock<IProjectManager>();
        projectManager.Setup(x => x.FindProjectFilePath(It.IsAny<string>()))
            .Returns(projectFilePath is null
                ? new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Missing())
                : new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Found(projectFilePath)));

        return new GodotCommand(
            _versionService.Object,
            _argumentService.Object,
            _launcher.Object,
            projectManager.Object,
            _registry.Object,
            _console,
            new Mock<ILogger<GodotCommand>>().Object);
    }
}
