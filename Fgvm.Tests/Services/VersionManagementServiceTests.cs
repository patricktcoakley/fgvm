using System.Runtime.InteropServices;
using System.Security;
using Fgvm.Cli;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Tests.Godot.ReleaseManager;
using Fgvm.Tests.Progress;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console.Testing;
using RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Services;

public class VersionManagementServiceTests
{
    private readonly TestConsole _console;
    private readonly TestConsole _diagnosticConsole;
    private readonly Mock<IHostSystem> _mockHostSystem;
    private readonly Mock<IInstallationRegistry> _mockInstallationRegistry;
    private readonly Mock<IInstallationService> _mockInstallationService;
    private readonly Mock<IProjectManager> _mockProjectManager;
    private readonly Mock<IReleaseManager> _mockReleaseManager;
    private readonly VersionManagementService _service;

    public VersionManagementServiceTests()
    {
        _mockHostSystem = new Mock<IHostSystem>();
        _mockReleaseManager = new Mock<IReleaseManager>();
        _mockInstallationRegistry = new Mock<IInstallationRegistry>();
        var mockPathService = new Mock<IPathService>();
        _mockInstallationService = new Mock<IInstallationService>();
        _mockProjectManager = new Mock<IProjectManager>();
        var mockLogger = new Mock<ILogger<VersionManagementService>>();

        _console = new TestConsole();
        _diagnosticConsole = new TestConsole();
        var installFlowProgressHandler = new SilentProgressHandler();

        mockPathService.Setup(x => x.RootPath).Returns("/test/fgvm");
        mockPathService.Setup(x => x.ReleasesPath).Returns("/test/fgvm/releases.json");
        mockPathService.Setup(x => x.InstallationsPath).Returns("/test/fgvm/installations.json");
        mockPathService.Setup(x => x.InstallationsDirectoryPath).Returns("/test/fgvm/installations");
        mockPathService.Setup(x => x.BinPath).Returns("/test/fgvm/bin");
        mockPathService.Setup(x => x.ShimPath).Returns("/test/fgvm/bin/godot");
        mockPathService.Setup(x => x.SymlinkPath).Returns("/test/fgvm/Godot");
        mockPathService.Setup(x => x.MacAppSymlinkPath).Returns("/test/fgvm/Godot.app");
        mockPathService.Setup(x => x.LogPath).Returns("/test/fgvm/.log");

        // Default mock setup - tests can override this
        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectMissing());
        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectMissing());

        _mockInstallationService.Setup(x => x.FetchReleaseNames(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()))
            .Returns(new Result<Release, QueryError>.Failure(new QueryError.NotFound("test")));

        _mockHostSystem.Setup(x => x.SystemInfo)
            .Returns(new SystemInfo(OS.Linux, Architecture.X64));

        _mockHostSystem.Setup(x => x.EnsureShim(It.IsAny<string>()))
            .Returns(new Result<Unit, ShimError>.Success(Unit.Value));

        _mockHostSystem.Setup(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()))
            .Returns(new Result<Unit, SymlinkError>.Success(Unit.Value));

        SetupInstallations([]);
        _mockInstallationRegistry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("default")));

        var installationOrchestrator = new InstallationOrchestrator(
            _mockReleaseManager.Object,
            _mockInstallationRegistry.Object,
            _mockInstallationService.Object,
            installFlowProgressHandler,
            _console);

        _service = new VersionManagementService(
            _mockHostSystem.Object,
            _mockReleaseManager.Object,
            _mockInstallationRegistry.Object,
            _mockInstallationService.Object,
            installationOrchestrator,
            mockPathService.Object,
            _mockProjectManager.Object,
            _console,
            new DiagnosticConsole(_diagnosticConsole),
            mockLogger.Object
        );
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithNoProjectAndNoInstallations_ReturnsNotFound()
    {
        SetupInstallations([]);

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Failure { Error: VersionResolutionError.NotFound });
        var hasProjectMessage = _diagnosticConsole.Output.Contains("Project requires") ||
                                _diagnosticConsole.Output.Contains("Project specifies");
        var hasNoInstallationMessage = _diagnosticConsole.Output.Contains("No Godot versions installed");
        var hasNoVersionSetMessage = _diagnosticConsole.Output.Contains("No current Godot version set");
        var hasNotFoundMessage = _diagnosticConsole.Output.Contains("could not be found");

        Assert.True(hasProjectMessage || hasNoInstallationMessage || hasNoVersionSetMessage || hasNotFoundMessage,
            $"Expected either project-specific, no installations, no version set, or not found message. Actual output: {_diagnosticConsole.Output}");
        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithProjectVersion_ReturnsCorrectResult()
    {
        const string projectVersion = "4.5.0";
        const string compatibleVersion = "4.5.1-stable-standard";
        var installedVersions = new[] { compatibleVersion };

        // Mock project info for this test
        if (Release.TryParse($"{projectVersion}-stable-standard") is not { } projectRelease)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));

        SetupInstallations(installedVersions);
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockReleaseManager.Setup(x =>
                x.FindCompatibleVersionResult(It.IsAny<string>(), false, installedVersions))
            .Returns((string version, bool isDotNet, IEnumerable<string> installed) =>
                releaseManager.FindCompatibleVersionResult(version, isDotNet, installed));

        var mockRelease = CreateMockRelease(compatibleVersion);
        _mockReleaseManager.Setup(x => x.CreateRelease(compatibleVersion))
            .Returns(mockRelease);

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Success);
        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.True(success.Value is VersionResolutionOutcome.Found);
        var found = (VersionResolutionOutcome.Found)success.Value;
        Assert.Equal(compatibleVersion, found.VersionName);
        Assert.Contains(compatibleVersion, found.ExecutablePath);
        Assert.Contains(compatibleVersion, found.WorkingDirectory);
        Assert.True(found.IsProjectVersion);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_ProjectVersionNotInstalled_PromptsForInstallation()
    {
        const string projectVersion = "4.3.0";
        var installedVersions = Array.Empty<string>();

        // Mock project info for this test
        if (Release.TryParse($"{projectVersion}-stable-standard") is not { } projectRelease)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, false, installedVersions))
            .Returns(CompatibleVersion(null));

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Failure { Error: VersionResolutionError.NotFound });
        Assert.True(_diagnosticConsole.Output.Contains("Project specifies") ||
                    _diagnosticConsole.Output.Contains("Project requires") ||
                    _diagnosticConsole.Output.Contains("could not be found"),
            $"Expected project or not found message. Actual: {_diagnosticConsole.Output}");
        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_ForceInteractive_PromptsForSelection()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        SetupInstallations(installedVersions);

        const string selectedVersion = "4.3.0-stable";
        var mockRelease = CreateMockRelease(selectedVersion);
        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(mockRelease);

        await _service.ResolveVersionForLaunchAsync(true);

        _mockInstallationRegistry.Verify(x => x.ListInstallations(), Times.Once);
        Assert.DoesNotContain("No Godot versions installed", _console.Output);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_MacOSAppBundle_HandlesCorrectly()
    {
        const string projectVersion = "4.3.0";
        const string compatibleVersion = "4.3.0-stable-standard";
        const string execName = "Godot.app";
        var installedVersions = new[] { compatibleVersion };

        // Mock project info for this test
        if (Release.TryParse($"{projectVersion}-stable-standard") is not { } projectRelease)
        {
            throw new InvalidOperationException("Expected release to parse.");
        }

        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x =>
                x.FindCompatibleVersionResult(projectVersion, false, installedVersions))
            .Returns(compatibleVersion);

        var mockRelease = CreateMockRelease(compatibleVersion, execName);
        _mockReleaseManager.Setup(x => x.CreateRelease(compatibleVersion))
            .Returns(mockRelease);

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Success);
        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.True(success.Value is VersionResolutionOutcome.Found);
        var found = (VersionResolutionOutcome.Found)success.Value;
        Assert.Equal(compatibleVersion, found.VersionName);
        Assert.Contains(compatibleVersion, found.ExecutablePath);
        Assert.Contains(compatibleVersion, found.WorkingDirectory);
        Assert.True(found.IsProjectVersion);
    }

    [Theory]
    [InlineData(false, "4.5-stable")]
    [InlineData(true, "4.5-stable")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    public async Task ResolveVersionForLaunch_ForceInteractiveDoesNotReadThePin(bool explicitOnly, string pin)
    {
        string[] installed = ["4.5-stable-standard", "4.5.1-stable-standard"];
        SetupInstallations(installed);
        var invalidPin = new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidVersion(pin));
        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>())).Returns(invalidPin);
        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>())).Returns(invalidPin);

        var result = explicitOnly
            ? await _service.ResolveVersionForLaunchExplicitAsync(forceInteractive: true)
            : await _service.ResolveVersionForLaunchAsync(forceInteractive: true);

        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.Equal(installed, Assert.IsType<VersionResolutionOutcome.InteractiveRequired>(success.Value).AvailableVersions);
        _mockProjectManager.Verify(x => x.FindExplicitProjectInfo(It.IsAny<string>()), Times.Never);
        _mockProjectManager.Verify(x => x.FindProjectInfo(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveEffectiveVersionAsync_WithExplicitProjectVersion_ReturnsProjectVersionWithoutOutput()
    {
        const string defaultVersion = "4.6.2-stable-standard";
        const string localVersion = "4.5-stable-standard";
        var installedVersions = new[] { defaultVersion, localVersion };
        var defaultInstallation = CreateInstallation(defaultVersion);
        var projectRelease = CreateMockRelease(localVersion);

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations(installedVersions);
        _mockInstallationRegistry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Success(defaultInstallation));
        _mockReleaseManager.Setup(x => x.CreateRelease(localVersion))
            .Returns(CreateMockRelease(localVersion));

        var result = await _service.ResolveEffectiveVersionAsync();

        var success = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success>(result);
        Assert.Equal(localVersion, success.Value.VersionName);
        Assert.True(success.Value.IsProjectVersion);
        Assert.Contains(localVersion, success.Value.ExecutablePath);
        Assert.Empty(_console.Output);
        _mockInstallationRegistry.Verify(x => x.GetDefault(), Times.Never);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task ResolveEffectiveVersionAsync_ExplicitPinDoesNotAcceptANewerPatch()
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations([nearbyVersion]);

        var result = await _service.ResolveEffectiveVersionAsync();

        var failure = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure>(result);
        var notFound = Assert.IsType<VersionResolutionError.NotFound>(failure.Error);
        Assert.Equal(pinnedVersion, notFound.Version);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task ResolveVersionForLaunchExplicitAsync_UsesTheExactPinnedInstallation()
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations([nearbyVersion, pinnedVersion]);
        SetupReleaseParsing([nearbyVersion, pinnedVersion]);

        var result = await _service.ResolveVersionForLaunchExplicitAsync();

        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        var found = Assert.IsType<VersionResolutionOutcome.Found>(success.Value);
        Assert.Equal(pinnedVersion, found.VersionName);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_ExplicitPinDoesNotUseANearbyInstalledPatch()
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations([nearbyVersion]);

        var result = await _service.ResolveVersionForLaunchAsync();

        var failure = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Failure>(result);
        var notFound = Assert.IsType<VersionResolutionError.NotFound>(failure.Error);
        Assert.Equal(pinnedVersion, notFound.Version);
        Assert.Contains($"fgvm install {pinnedVersion}", _diagnosticConsole.Output);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
        _mockInstallationService.Verify(x => x.InstallReleaseAsync(
            It.IsAny<Release>(),
            It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockProjectManager.Verify(x => x.FindProjectInfo(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveVersionForLaunchExplicitAsync_InstallsOnlyTheExactPinWhenConfirmed()
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);
        var pinnedInstallation = CreateInstallation(pinnedVersion);
        var query = new[] { pinnedVersion };
        var releaseNames = new[] { "4.5-stable", "4.5.1-stable" };

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallationsSequence(
            [nearbyVersion],
            [nearbyVersion],
            [nearbyVersion],
            [nearbyVersion, pinnedVersion]);
        _mockInstallationRegistry.SetupSequence(x => x.FindByReleaseName(pinnedVersion))
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(
                new InstallationRegistryError.NotFound(pinnedVersion)))
            .Returns(new Result<Installation, InstallationRegistryError>.Success(pinnedInstallation));
        _mockInstallationService.Setup(x => x.FetchReleaseNames(
                It.IsAny<CancellationToken>(), ReleaseFetchMode.UseCache))
            .ReturnsAsync(releaseNames);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(
                It.Is<string[]>(value => value.SequenceEqual(query)),
                It.Is<string[]>(available => available.SequenceEqual(releaseNames))))
            .Returns(new Result<Release, QueryError>.Success(projectRelease));
        _mockInstallationService.Setup(x => x.InstallReleaseAsync(
                projectRelease,
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(pinnedVersion, new ChecksumVerification.Verified())));
        _mockReleaseManager.Setup(x => x.CreateRelease(pinnedVersion))
            .Returns(ReleaseSuccess(projectRelease));
        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.ResolveVersionForLaunchExplicitAsync();

        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        var found = Assert.IsType<VersionResolutionOutcome.Found>(success.Value);
        Assert.Equal(pinnedVersion, found.VersionName);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "standard")]
    [InlineData(true, "mono")]
    [InlineData(false, "standard")]
    [InlineData(false, "mono")]
    public async Task ResolveVersionForLaunchAsync_AutoInstallFailurePreservesTheRequestedVersionInDiagnostics(bool exact, string runtime)
    {
        var projectVersion = $"4.5-stable-{runtime}";
        var projectRelease = CreateMockRelease(projectVersion);
        string[] query = (exact, runtime) switch
        {
            (true, _) => [projectVersion],
            (false, "mono") => ["4.5", "mono"],
            _ => ["4.5"]
        };
        SetupInstallations([$"4.6-stable-{runtime}"]);
        if (exact)
        {
            _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
                .Returns(ProjectFound(projectRelease));
        }
        else
        {
            _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
                .Returns(ProjectFound(projectRelease));
            _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult("4.5", projectRelease.IsDotNet, It.IsAny<IEnumerable<string>>()))
                .Returns(CompatibleVersion(null));
        }

        _mockInstallationService.Setup(x => x.FetchReleaseNames(It.IsAny<CancellationToken>(), ReleaseFetchMode.UseCache))
            .ReturnsAsync(new[] { projectRelease.ReleaseName });
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(
                It.Is<string[]>(value => value.SequenceEqual(query)), It.IsAny<string[]>()))
            .Returns(new Result<Release, QueryError>.Success(projectRelease));
        _mockInstallationService.Setup(x => x.InstallReleaseAsync(
                projectRelease, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(new InstallationError.Failed("network unavailable")));
        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = exact
            ? await _service.ResolveVersionForLaunchExplicitAsync()
            : await _service.ResolveVersionForLaunchAsync();

        var failure = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Failure>(result);
        Assert.Contains("network unavailable", Assert.IsType<VersionResolutionError.Failed>(failure.Error).Reason);
        Assert.Contains($"You can manually install with: fgvm install {projectVersion}", _diagnosticConsole.Output);
        Assert.DoesNotContain("4.6", _diagnosticConsole.Output);
        _mockInstallationService.Verify(x => x.InstallReleaseAsync(
                projectRelease, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), false, false, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockProjectManager.Verify(x => x.CreateVersionFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveEffectiveVersionAsync_WithoutExplicitProjectVersion_ReturnsDefaultWithoutOutput()
    {
        const string defaultVersion = "4.6.2-stable-standard";
        var installation = CreateInstallation(defaultVersion);

        SetupInstallations([defaultVersion]);
        _mockInstallationRegistry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));
        _mockReleaseManager.Setup(x => x.CreateRelease(defaultVersion))
            .Returns(CreateMockRelease(defaultVersion));

        var result = await _service.ResolveEffectiveVersionAsync();

        var success = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success>(result);
        Assert.Equal(defaultVersion, success.Value.VersionName);
        Assert.False(success.Value.IsProjectVersion);
        Assert.Contains(defaultVersion, success.Value.ExecutablePath);
        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task ResolveEffectiveVersionAsync_DoesNotInstallMissingProjectVersion()
    {
        const string localVersion = "4.5-stable-standard";
        var projectRelease = CreateMockRelease(localVersion);

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations([]);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(localVersion,
                false,
                It.Is<IEnumerable<string>>(versions => !versions.Any())))
            .Returns(CompatibleVersion(null));

        var result = await _service.ResolveEffectiveVersionAsync();

        var failure = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure>(result);
        var notFound = Assert.IsType<VersionResolutionError.NotFound>(failure.Error);
        Assert.Equal(localVersion, notFound.Version);
        Assert.Empty(_console.Output);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveInstalledVersionAsync_WithQuery_ReturnsMatchingInstalledVersion()
    {
        var query = new[] { "4.6", "mono" };
        const string matchedVersion = "4.6.2-stable-mono";
        var installedVersions = new[] { "4.6.2-stable-standard", matchedVersion, "4.5-stable-standard" };
        var installedReleaseNames = new[] { "4.6.2-stable", "4.5-stable" };
        var matchedRelease = CreateMockRelease(matchedVersion);

        SetupInstallations(installedVersions);
        SetupReleaseParsing(installedVersions);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(
                query,
                It.Is<string[]>(versions => versions.SequenceEqual(installedReleaseNames))))
            .Returns(QuerySuccess(matchedRelease));

        var result = await _service.ResolveInstalledVersionAsync(query);

        var success = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success>(result);
        Assert.Equal(matchedVersion, success.Value.VersionName);
        Assert.False(success.Value.IsProjectVersion);
        Assert.Contains(matchedVersion, success.Value.ExecutablePath);
        Assert.Contains(matchedVersion, success.Value.WorkingDirectory);
        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task ResolveInstalledVersionAsync_WithExactInstalledVersion_ReturnsExactMatch()
    {
        var query = new[] { "4.6.2-stable-standard" };
        var installedVersions = new[] { "4.6.2-stable-standard", "4.6.2-stable-mono" };

        SetupInstallations(installedVersions);
        SetupReleaseParsing(installedVersions);

        var result = await _service.ResolveInstalledVersionAsync(query);

        var success = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success>(result);
        Assert.Equal("4.6.2-stable-standard", success.Value.VersionName);
        Assert.Contains("4.6.2-stable-standard", success.Value.ExecutablePath);
        _mockReleaseManager.Verify(
            x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveInstalledVersionAsync_WhenQueryDoesNotMatch_ReturnsNotFoundWithoutInstalling()
    {
        var query = new[] { "9.9" };
        var installedVersions = new[] { "4.6.2-stable-standard" };
        var installedReleaseNames = new[] { "4.6.2-stable" };

        SetupInstallations(installedVersions);
        SetupReleaseParsing(installedVersions);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(
                query,
                It.Is<string[]>(versions => versions.SequenceEqual(installedReleaseNames))))
            .Returns(QueryNotFound("9.9"));

        var result = await _service.ResolveInstalledVersionAsync(query);

        var failure = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure>(result);
        var notFound = Assert.IsType<VersionResolutionError.NotFound>(failure.Error);
        Assert.Equal("9.9", notFound.Version);
        Assert.Empty(_console.Output);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void CreateOrUpdateVersionFile_CallsProjectManager()
    {
        const string version = "4.3.0-stable-standard";
        var directory = Path.GetTempPath();

        var versionFilePath = Path.Combine(directory, ".fgvm-version");

        // Mock CreateVersionFile to actually create the file
        _mockProjectManager.Setup(x => x.CreateVersionFile(version, directory))
            .Callback<string, string>((v, d) =>
            {
                var filePath = Path.Combine(d ?? Directory.GetCurrentDirectory(), ".fgvm-version");
                File.WriteAllText(filePath, v + System.Environment.NewLine);
            });

        try
        {
            _service.CreateOrUpdateVersionFile(version, directory);

            Assert.True(File.Exists(versionFilePath));

            var content = File.ReadAllText(versionFilePath);
            Assert.Equal(version, content.Trim());
        }
        finally
        {
            if (File.Exists(versionFilePath))
            {
                File.Delete(versionFilePath);
            }
        }
    }

    private static Release CreateMockRelease(string versionString, string execName = "Godot")
    {
        var parts = versionString.Split(['-', '.'], StringSplitOptions.RemoveEmptyEntries);
        var major = int.Parse(parts[0]);
        var minor = int.Parse(parts[1]);
        int? patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : null;

        var releaseType = versionString.Contains("stable") ? ReleaseType.Stable() :
            versionString.Contains("rc") ? ReleaseType.Rc(1) :
            versionString.Contains("beta") ? ReleaseType.Beta(1) :
            versionString.Contains("alpha") ? ReleaseType.Alpha(1) :
            ReleaseType.Stable();

        var runtime = versionString.Contains("mono") ? RuntimeEnvironment.Mono : RuntimeEnvironment.Standard;

        var release = new Release(major, minor, patch: patch, type: releaseType, runtimeEnvironment: runtime);

        if (execName is "Godot.app" or "Godot_mono.app")
        {
            return release with
            {
                OS = OS.MacOS,
                PlatformString = "macos.universal.zip"
            };
        }

        return release with
        {
            OS = OS.Windows,
            PlatformString = "win64.exe"
        };
    }

    [Fact]
    public async Task SetLocalVersionAsync_WithNoInstallationsAndNoQuery_ThrowsException()
    {
        SetupInstallations([]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetLocalVersionAsync(forceInteractive: true));

        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task SetLocalVersionAsync_WithBothRuntimes_HonorsExplicitStandardQuery()
    {
        var query = new[] { "4.6", "standard" };
        const string standardVersion = "4.6.2-stable-standard";
        var installedVersions = new[] { standardVersion, "4.6.2-stable-mono" };
        var installedReleaseNames = new[] { "4.6.2-stable" };
        var standardRelease = CreateMockRelease(standardVersion);

        SetupInstallations(installedVersions);
        SetupReleaseParsing(installedVersions);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(
                query,
                It.Is<string[]>(versions => versions.SequenceEqual(installedReleaseNames))))
            .Returns(standardRelease);

        var result = await _service.SetLocalVersionAsync(query);

        Assert.Equal(standardRelease, result);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(
                It.IsAny<string[]>(),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetLocalVersionAsync_VersionNotInstalled_AttemptsInstallation()
    {
        const string queryVersion = "4.3.0";
        const string newVersion = "4.3.0-stable";
        var query = new[] { queryVersion };
        var installedVersions = Array.Empty<string>();

        SetupInstallationsSequence(installedVersions, [newVersion]);

        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, Array.Empty<string>()))
            .Returns(QueryNotFound(string.Join(" ", query)));

        var mockRelease = CreateMockRelease(newVersion);
        var installationResult = new Result<InstallationOutcome, InstallationError>.Success(
            new InstallationOutcome.NewInstallation(mockRelease.ReleaseNameWithRuntime, new ChecksumVerification.Verified()));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(query, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(installationResult);

        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, new[] { newVersion }))
            .Returns(mockRelease);

        _mockReleaseManager.Setup(x => x.CreateRelease(mockRelease.ReleaseNameWithRuntime))
            .Returns(mockRelease);

        var result = await _service.SetLocalVersionAsync(query);

        Assert.Equal(mockRelease, result);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(query, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("No installed version found matching", _console.Output);
        Assert.Contains("Installing", _console.Output);
        Assert.Contains("Successfully installed", _console.Output);
        Assert.Contains("`.fgvm-version` file in current directory", _console.Output);
    }

    [Theory]
    [InlineData("4.5")]
    [InlineData("4.5-stable")]
    public async Task SetLocalVersionAsync_WritesTheResolvedReleaseInsteadOfTheQuery(string query)
    {
        const string resolvedVersion = "4.5.1-stable-standard";
        string[] installedVersions = ["4.5-stable-standard", resolvedVersion];
        SetupInstallations(installedVersions);
        SetupReleaseParsing(installedVersions);
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()))
            .Returns((string[] versionQuery, string[] releases) => releaseManager.ResolveReleaseQuery(versionQuery, releases));
        _mockProjectManager.Setup(x => x.CreateVersionFile(resolvedVersion, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));

        var result = await _service.SetLocalVersionAsync([query]);

        Assert.Equal(resolvedVersion, result.ReleaseNameWithRuntime);
        _mockProjectManager.Verify(x => x.CreateVersionFile(resolvedVersion, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SetLocalVersionAsync_ExistingPinInstallsThatExactRelease()
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);
        var installResult = new Result<InstallationOutcome, InstallationError>.Success(
            new InstallationOutcome.NewInstallation(pinnedVersion, new ChecksumVerification.Verified()));

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        _mockProjectManager.Setup(x => x.CreateVersionFile(pinnedVersion, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));
        SetupInstallations([nearbyVersion]);
        _mockInstallationService.Setup(x => x.InstallByQueryAsync(
                It.Is<string[]>(query => query.SequenceEqual(new[] { pinnedVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(installResult);
        _mockReleaseManager.Setup(x => x.CreateRelease(pinnedVersion))
            .Returns(ReleaseSuccess(projectRelease));

        var result = await _service.SetLocalVersionAsync();

        Assert.Equal(pinnedVersion, result.ReleaseNameWithRuntime);
        _mockInstallationService.Verify(x => x.InstallByQueryAsync(
            It.Is<string[]>(query => query.SequenceEqual(new[] { pinnedVersion })),
            It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
            false,
            false,
            It.IsAny<CancellationToken>()), Times.Once);
        _mockProjectManager.Verify(x => x.FindProjectInfo(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("4.5-stable-standard", "4.5.1-stable-standard")]
    [InlineData("4.5.0-stable-standard", "4.5.1-stable-standard")]
    [InlineData("4.5-stable-mono", "4.5.1-stable-mono")]
    [InlineData("4.5.1-stable-standard", "4.5.2-stable-standard")]
    [InlineData("4.5.1-stable-mono", "4.5.2-stable-mono")]
    public async Task SetLocalVersionAsync_ProjectGodotStillAcceptsACompatiblePatch(string projectVersion, string compatibleVersion)
    {
        var projectRelease = CreateMockRelease(projectVersion);
        var compatibleRelease = CreateMockRelease(compatibleVersion);
        var installedVersions = new[] { compatibleVersion, projectRelease.IsDotNet ? "4.5.2-stable-standard" : "4.5.2-stable-mono" };

        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        _mockProjectManager.Setup(x => x.CreateVersionFile(compatibleVersion, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));
        SetupInstallations(installedVersions);
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(It.IsAny<string>(), It.IsAny<bool>(), installedVersions))
            .Returns((string version, bool isDotNet, IEnumerable<string> installed) =>
                releaseManager.FindCompatibleVersionResult(version, isDotNet, installed));
        _mockReleaseManager.Setup(x => x.CreateRelease(compatibleVersion))
            .Returns(ReleaseSuccess(compatibleRelease));

        var result = await _service.SetLocalVersionAsync();

        Assert.Equal(compatibleVersion, result.ReleaseNameWithRuntime);
        var launchResult = await _service.ResolveVersionForLaunchAsync();
        var launchSuccess = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(launchResult);
        Assert.Equal(compatibleVersion, Assert.IsType<VersionResolutionOutcome.Found>(launchSuccess.Value).VersionName);
        _mockInstallationService.Verify(x => x.InstallByQueryAsync(
            It.IsAny<string[]>(),
            It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("mono")]
    public async Task ProjectPatchRequirementDoesNotUseAnOlderInstalledVersion(string runtime)
    {
        var required = $"4.3.1-stable-{runtime}";
        var installed = $"4.3-stable-{runtime}";
        var projectRelease = CreateMockRelease(required);
        SetupInstallations([installed]);
        SetupReleaseParsing([installed, required]);
        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>())).Returns(ProjectFound(projectRelease));
        _mockProjectManager.Setup(x => x.CreateVersionFile(required, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()))
            .Returns((string version, bool isDotNet, IEnumerable<string> versions) =>
                releaseManager.FindCompatibleVersionResult(version, isDotNet, versions));
        _mockInstallationService.Setup(x => x.InstallByQueryAsync(
                It.Is<string[]>(query => query.SequenceEqual(new[] { "4.3.1", "stable", runtime })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(required, new ChecksumVerification.Verified())));

        var launch = await _service.ResolveVersionForLaunchAsync();

        var failure = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Failure>(launch);
        Assert.Equal(required, Assert.IsType<VersionResolutionError.NotFound>(failure.Error).Version);

        var local = await _service.SetLocalVersionAsync();

        Assert.Equal(required, local.ReleaseNameWithRuntime);
        _mockProjectManager.Verify(x => x.CreateVersionFile(required, It.IsAny<string>()), Times.Once);
        _mockProjectManager.Verify(x => x.CreateVersionFile(installed, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SetLocalVersionAsync_ProjectGodotInstallsACompatiblePatch()
    {
        const string projectVersion = "4.5-stable-mono";
        const string compatibleVersion = "4.5.1-stable-mono";
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(CreateMockRelease(projectVersion)));
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult("4.5", true, It.IsAny<IEnumerable<string>>()))
            .Returns(new Result<string, CompatibilityError>.Failure(new CompatibilityError.NoInstalledVersions()));
        _mockReleaseManager.Setup(x => x.CreateRelease(compatibleVersion))
            .Returns(ReleaseSuccess(CreateMockRelease(compatibleVersion)));
        _mockProjectManager.Setup(x => x.CreateVersionFile(compatibleVersion, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));
        _mockInstallationService.Setup(x => x.InstallByQueryAsync(
                It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), false, false,
                It.IsAny<CancellationToken>()))
            .Returns((string[] query, IProgress<OperationProgress<InstallationStage>> _, bool _, bool _, CancellationToken _) =>
            {
                var resolution = releaseManager.ResolveReleaseQuery(query, ["4.5-stable", "4.5.1-stable"]);
                var release = Assert.IsType<Result<Release, QueryError>.Success>(resolution).Value;
                return Task.FromResult<Result<InstallationOutcome, InstallationError>>(
                    new Result<InstallationOutcome, InstallationError>.Success(
                        new InstallationOutcome.NewInstallation(release.ReleaseNameWithRuntime, new ChecksumVerification.Verified())));
            });

        var result = await _service.SetLocalVersionAsync();

        Assert.Equal(compatibleVersion, result.ReleaseNameWithRuntime);
        _mockProjectManager.Verify(x => x.CreateVersionFile(compatibleVersion, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData("network", "network unavailable")]
    [InlineData("checksum", "Checksum mismatch")]
    [InlineData("not-found", "could not be found")]
    [InlineData("invalid-query", "invalid query")]
    public async Task SetLocalVersionAsync_ExactPinInstallationFailureDoesNotReplaceThePin(string failureKind, string expectedMessage)
    {
        const string pinnedVersion = "4.5-stable-standard";
        const string nearbyVersion = "4.5.1-stable-standard";
        var projectRelease = CreateMockRelease(pinnedVersion);
        InstallationError error = failureKind switch
        {
            "checksum" => new InstallationError.ChecksumMismatch("expected", "actual", "Godot.zip"),
            "not-found" => new InstallationError.NotFound(pinnedVersion),
            "invalid-query" => new InstallationError.InvalidQuery(expectedMessage),
            _ => new InstallationError.Failed(expectedMessage)
        };

        _mockProjectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(ProjectFound(projectRelease));
        SetupInstallations([nearbyVersion]);
        _mockInstallationService.Setup(x => x.InstallByQueryAsync(
                It.Is<string[]>(query => query.SequenceEqual(new[] { pinnedVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(error));

        var exception = await Record.ExceptionAsync(() => _service.SetLocalVersionAsync());

        switch (failureKind)
        {
            case "checksum":
                Assert.IsType<SecurityException>(exception);
                break;
            case "not-found" or "invalid-query":
                Assert.IsType<ArgumentException>(exception);
                break;
            default:
                Assert.IsType<InvalidOperationException>(exception);
                Assert.Contains(pinnedVersion, exception.Message);
                break;
        }

        Assert.NotNull(exception);
        Assert.Contains(expectedMessage, exception.Message);
        Assert.DoesNotContain("[red]", exception.Message);
        Assert.DoesNotContain("[/]", exception.Message);
        _mockProjectManager.Verify(x => x.CreateVersionFile(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Theory]
    [InlineData("4.5", false)]
    [InlineData("4.5-stable", false)]
    [InlineData("4.5-stable-standard", false)]
    [InlineData("4.5-stable-standard", true)]
    public async Task SetLocalVersionAsync_InstallationFailureOnlyOffersAnotherVersionForFuzzyQueries(string query, bool checksumFailure)
    {
        const string installedVersion = "4.6-stable-standard";
        SetupInstallations([installedVersion]);
        SetupReleaseParsing([installedVersion]);
        var releaseManager = new ReleaseManagerBuilder().Build();
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()))
            .Returns((string[] versionQuery, string[] releases) => releaseManager.ResolveReleaseQuery(versionQuery, releases));
        _mockProjectManager.Setup(x => x.CreateVersionFile(installedVersion, It.IsAny<string>()))
            .Returns(new Result<Unit, ProjectError>.Success(Unit.Value));
        _mockInstallationService.Setup(x => x.InstallByQueryAsync(
                It.Is<string[]>(value => value.SequenceEqual(new[] { query })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(checksumFailure
                ? new InstallationError.ChecksumMismatch("expected", "actual", "Godot.zip")
                : new InstallationError.Failed("network unavailable")));
        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var exception = await Record.ExceptionAsync(() => _service.SetLocalVersionAsync([query]));

        if (query == "4.5-stable-standard")
        {
            if (checksumFailure)
            {
                Assert.Contains("Godot.zip", Assert.IsType<SecurityException>(exception).Message);
            }
            else
            {
                Assert.Contains("network unavailable", Assert.IsType<InvalidOperationException>(exception).Message);
            }

            _mockProjectManager.Verify(x => x.CreateVersionFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        else
        {
            Assert.Null(exception);
            _mockProjectManager.Verify(x => x.CreateVersionFile(installedVersion, It.IsAny<string>()), Times.Once);
        }
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_CompatibleVersionExists_ReturnsFound()
    {
        const string projectVersion = "4.3.0";
        const string compatibleVersion = "4.3.0-stable";
        var installedVersions = new[] { compatibleVersion };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, false, installedVersions))
            .Returns(compatibleVersion);

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false);

        var success = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Success>(result);
        var found = Assert.IsType<CompatibleVersionOutcome.Found>(success.Value);
        Assert.Equal(compatibleVersion, found.Version);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_InstallationFails_ReturnsTypedError()
    {
        const string projectVersion = "4.3.0";
        var installedVersions = Array.Empty<string>();

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, false, installedVersions))
            .Returns(CompatibleVersion(null));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.NotFound(projectVersion)));

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false, false);

        var failure = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Failure>(result);
        var installationFailed = Assert.IsType<CompatibleVersionError.InstallationFailed>(failure.Error);
        var notFound = Assert.IsType<InstallationError.NotFound>(installationFailed.Error);
        Assert.Equal(projectVersion, notFound.Version);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("Installing", _console.Output);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_InstallationSucceeds_ReturnsInstalled()
    {
        const string projectVersion = "4.3.0";
        const string compatibleVersion = "4.3.0-stable";
        var initialInstalled = Array.Empty<string>();
        var postInstallInstalled = new[] { compatibleVersion };

        SetupInstallationsSequence(initialInstalled, initialInstalled, postInstallInstalled);

        _mockReleaseManager.SetupSequence(x => x.FindCompatibleVersionResult(projectVersion, false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null))
            .Returns(compatibleVersion);

        var mockRelease = CreateMockRelease(compatibleVersion);
        var installationResult = new Result<InstallationOutcome, InstallationError>.Success(
            new InstallationOutcome.NewInstallation(mockRelease.ReleaseNameWithRuntime, new ChecksumVerification.Verified()));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(installationResult);

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false);

        var success = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Success>(result);
        var installed = Assert.IsType<CompatibleVersionOutcome.Installed>(success.Value);
        Assert.Equal(compatibleVersion, installed.Version);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("Finished installing", _console.Output);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_DotNetProject_UsesCorrectQuery()
    {
        const string projectVersion = "4.3.0-stable-mono";
        var installedVersions = Array.Empty<string>();

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, true, installedVersions))
            .Returns(CompatibleVersion(null));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { "4.3.0-stable", "mono" })),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.NotFound(projectVersion)));

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, true, false);

        var failure = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Failure>(result);
        Assert.IsType<CompatibleVersionError.InstallationFailed>(failure.Error);
        _mockReleaseManager.Verify(x => x.FindCompatibleVersionResult(projectVersion, true, installedVersions), Times.Once);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { "4.3.0-stable", "mono" })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_WithPromptConfirmed_InstallsVersion()
    {
        const string projectVersion = "4.3.0";
        const string compatibleVersion = "4.3.0-stable";
        var initialInstalled = Array.Empty<string>();
        var postInstallInstalled = new[] { compatibleVersion };

        SetupInstallationsSequence(initialInstalled, initialInstalled, postInstallInstalled);

        _mockReleaseManager.SetupSequence(x => x.FindCompatibleVersionResult(projectVersion, false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null))
            .Returns(compatibleVersion);

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var mockRelease = CreateMockRelease(compatibleVersion);
        var installationResult = new Result<InstallationOutcome, InstallationError>.Success(
            new InstallationOutcome.NewInstallation(mockRelease.ReleaseNameWithRuntime, new ChecksumVerification.Verified()));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(installationResult);

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false);

        var success = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Success>(result);
        var installed = Assert.IsType<CompatibleVersionOutcome.Installed>(success.Value);
        Assert.Equal(compatibleVersion, installed.Version);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("Project requires", _console.Output);
        Assert.Contains("Installing", _console.Output);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_WithPromptDeclined_ReturnsDeclined()
    {
        const string projectVersion = "4.3.0";
        SetupInstallations([]);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null));

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.N);
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false);

        var success = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Success>(result);
        Assert.IsType<CompatibleVersionOutcome.Declined>(success.Value);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_PromptCancellation_RemainsCancellation()
    {
        SetupInstallations([]);
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult("4.3.0", false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.FindOrInstallCompatibleVersionAsync("4.3.0", false, cancellationToken: cancellation.Token));

        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_RegistryReadFails_ReturnsTypedError()
    {
        var registryError = new InstallationRegistryError.ReadFailed(new FileOperationError.IoFailure("installations.json"));
        _mockInstallationRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure(registryError));

        var result = await _service.FindOrInstallCompatibleVersionAsync("4.3", false, false);

        var failure = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Failure>(result);
        var registryFailed = Assert.IsType<CompatibleVersionError.RegistryFailed>(failure.Error);
        Assert.Equal(registryError, registryFailed.Error);
    }

    [Fact]
    public async Task FindOrInstallCompatibleVersionAsync_PostInstallCompatibilityFails_ReturnsResolutionError()
    {
        const string projectVersion = "4.3.0";
        const string installedVersion = "4.3.0-stable-standard";
        var initialInstalled = Array.Empty<string>();
        var postInstallInstalled = new[] { installedVersion };

        SetupInstallationsSequence(initialInstalled, initialInstalled, postInstallInstalled);
        _mockReleaseManager.SetupSequence(x => x.FindCompatibleVersionResult(
                projectVersion, false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null))
            .Returns(new Result<string, CompatibilityError>.Failure(
                new CompatibilityError.NoInstalledVersions()));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(installedVersion, new ChecksumVerification.Verified())));

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false, false);

        var failure = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Failure>(result);
        var resolutionFailed = Assert.IsType<CompatibleVersionError.ResolutionFailed>(failure.Error);
        Assert.Equal(projectVersion, resolutionFailed.ProjectVersion);
        Assert.Equal(installedVersion, resolutionFailed.InstalledVersion);
        Assert.IsType<CompatibilityError.NoInstalledVersions>(resolutionFailed.Error);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithNoInstallations_ThrowsException()
    {
        SetupInstallations([]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetGlobalVersionAsync(["4.3.0"]));

        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithValidQuery_SetsVersionSuccessfully()
    {
        const string queryVersion = "4.3.0";
        const string matchedVersion = "4.3.0-stable";
        var query = new[] { queryVersion };
        var installedVersions = new[] { matchedVersion, "4.2.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns([matchedVersion]);

        var mockRelease = CreateMockRelease(matchedVersion);
        _mockReleaseManager.Setup(x => x.CreateRelease(matchedVersion))
            .Returns(mockRelease);

        _mockHostSystem.Setup(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()))
            .Returns(new Result<Unit, SymlinkError>.Success(Unit.Value));

        var result = await _service.SetGlobalVersionAsync(query);

        Assert.Equal(mockRelease, result);
        _mockHostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Once);
        _mockHostSystem.Verify(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()), Times.Once);

        Assert.Contains("Successfully set version to", _console.Output);
        Assert.Contains(matchedVersion, _console.Output);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithInvalidQuery_ThrowsException()
    {
        const string invalidVersion = "invalid-version";
        var query = new[] { invalidVersion };
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns([]);

        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetGlobalVersionAsync(query));

        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task SetLocalVersionAsync_WithInvalidQuery_ThrowsArgumentExceptionWithoutInstalling()
    {
        var query = new[] { "bad-query" };
        var installedVersions = new[] { "4.3.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, installedVersions))
            .Returns(new Result<Release, QueryError>.Failure(new QueryError.InvalidQuery("Invalid query.")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.SetLocalVersionAsync(query));

        Assert.Contains("Invalid query.", exception.Message);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(
                It.IsAny<string[]>(),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                It.IsAny<bool>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithEmptyQuery_UsesPrompt()
    {
        const string selectedVersion = "4.3.0-stable";
        var installedVersions = new[] { selectedVersion, "4.2.0-stable" };

        SetupInstallations(installedVersions);

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var mockRelease = CreateMockRelease(selectedVersion);
        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(mockRelease);

        _mockHostSystem.Setup(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()))
            .Returns(new Result<Unit, SymlinkError>.Success(Unit.Value));

        var result = await _service.SetGlobalVersionAsync([]);

        Assert.Equal(mockRelease, result);
        _mockInstallationRegistry.Verify(x => x.ListInstallations(), Times.Once);
        _mockHostSystem.Verify(x => x.EnsureShim(It.IsAny<string>()), Times.Once);
        _mockHostSystem.Verify(x => x.CreateOrOverwriteShortcut(It.IsAny<string>()), Times.Once);

        Assert.Contains("Successfully set version to", _console.Output);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithInvalidVersion_ThrowsInvalidOperationException()
    {
        const string invalidVersion = "invalid-version";
        var query = new[] { invalidVersion };
        var installedVersions = new[] { "4.3.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns([invalidVersion]);

        _mockReleaseManager.Setup(x => x.CreateRelease(invalidVersion))
            .Returns(ReleaseFailure(invalidVersion));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetGlobalVersionAsync(query));

        Assert.Equal("Invalid Godot version.", exception.Message);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithNoProjectFile_FallsBackToSymlink()
    {
        const string installedVersion = "4.3.0-stable";
        var installedVersions = new[] { installedVersion };
        var mockRelease = CreateMockRelease(installedVersion);

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.CreateRelease(installedVersion))
            .Returns(mockRelease);

        await _service.ResolveVersionForLaunchAsync();

        _mockInstallationRegistry.Verify(x => x.ListInstallations(), Times.Once);

        var hasValidOutput = _console.Output.Contains("No current Godot version set") ||
                             _console.Output.Contains("Using project version") || _console.Output.Contains("Project requires") ||
                             _console.Output.Contains("Error resolving") ||
                             _console.Output.Length == 0;

        Assert.True(hasValidOutput, $"Expected valid project or symlink message. Actual output: {_console.Output}");
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithNoInstallationsAndInteractive_ReturnsNull()
    {
        SetupInstallations([]);

        var result = await _service.ResolveVersionForLaunchAsync(true);

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Success);
        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.True(success.Value is VersionResolutionOutcome.InteractiveRequired);
        var interactive = (VersionResolutionOutcome.InteractiveRequired)success.Value;
        Assert.Empty(interactive.AvailableVersions);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithInstallationsAndInteractive_PromptsForSelection()
    {
        const string selectedVersion = "4.3.0-stable";
        var installedVersions = new[] { selectedVersion, "4.2.0-stable" };
        var mockRelease = CreateMockRelease(selectedVersion);

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(mockRelease);

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.ResolveVersionForLaunchAsync(true);

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Success);
        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.True(success.Value is VersionResolutionOutcome.InteractiveRequired);
        var interactive = (VersionResolutionOutcome.InteractiveRequired)success.Value;
        Assert.Equal(2, interactive.AvailableVersions.Count); // Using Assert.Equal since we need exactly 2
        Assert.Contains(selectedVersion, interactive.AvailableVersions);
        Assert.Contains("4.2.0-stable", interactive.AvailableVersions);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithInvalidSelectedVersion_ReturnsNull()
    {
        const string selectedVersion = "4.3.0-stable";
        var installedVersions = new[] { selectedVersion };

        SetupInstallations(installedVersions);

        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(ReleaseFailure(selectedVersion));

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.ResolveVersionForLaunchAsync(true);

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Success);
        var success = Assert.IsType<Result<VersionResolutionOutcome, VersionResolutionError>.Success>(result);
        Assert.True(success.Value is VersionResolutionOutcome.InteractiveRequired);
        var interactive = (VersionResolutionOutcome.InteractiveRequired)success.Value;
        Assert.Single(interactive.AvailableVersions);
        Assert.Contains(selectedVersion, interactive.AvailableVersions);
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_ExceptionThrown_ReturnsNullAndLogsError()
    {
        const string exceptionMessage = "Test exception";
        _mockInstallationRegistry.Setup(x => x.ListInstallations())
            .Throws(new InvalidOperationException(exceptionMessage));

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Failure { Error: VersionResolutionError.Failed });
        Assert.Contains("Error resolving Godot version for launch", _diagnosticConsole.Output);
        Assert.Empty(_console.Output);
    }

    [Fact]
    public async Task SetLocalVersionAsync_WithCancellationToken_HandlesCancellation()
    {
        const string queryVersion = "4.3.0";
        var query = new[] { queryVersion };
        var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        SetupInstallations([]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.SetLocalVersionAsync(query, cancellationToken: cancellationTokenSource.Token));
    }

    [Fact]
    public async Task SetLocalVersionAsync_ForceInteractive_ShowsPromptEvenWithInstallations()
    {
        const string selectedVersion = "4.3.0-stable";
        var installedVersions = new[] { selectedVersion };
        var mockRelease = CreateMockRelease(selectedVersion);

        SetupInstallations(installedVersions);

        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(mockRelease);

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.SetLocalVersionAsync(forceInteractive: true);

        Assert.Equal(mockRelease, result);
    }

    [Fact]
    public async Task SetLocalVersionAsync_InstallationFails_ThrowsException()
    {
        const string queryVersion = "4.3.0";
        const string errorMessage = "Installation failed";
        var query = new[] { queryVersion };

        SetupInstallations([]);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, Array.Empty<string>()))
            .Returns(QueryNotFound(string.Join(" ", query)));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(query, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(errorMessage));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetLocalVersionAsync(query));

        Assert.Contains(errorMessage, exception.Message);
    }

    [Fact]
    public async Task SetLocalVersionAsync_InstallationNotFound_ThrowsPlainTextArgumentException()
    {
        var query = new[] { "9.999" };

        SetupInstallations([]);
        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, Array.Empty<string>()))
            .Returns(QueryNotFound(query[0]));
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(query, It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Failure(
                new InstallationError.NotFound(query[0])));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _service.SetLocalVersionAsync(query));

        Assert.Equal("Version 9.999 could not be found for Linux x64", exception.Message);
        Assert.DoesNotContain("[red]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLocalVersionAsync_NoQueryProvided_PromptsForVersion()
    {
        const string selectedVersion = "4.3.0-stable";
        var installedVersions = new[] { selectedVersion };
        var mockRelease = CreateMockRelease(selectedVersion);

        SetupInstallations(installedVersions);

        _mockReleaseManager.Setup(x => x.CreateRelease(selectedVersion))
            .Returns(mockRelease);

        // Mock ProjectManager to return null (no project info found)
        _mockProjectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(ProjectMissing());

        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _service.SetLocalVersionAsync();

        Assert.Equal(mockRelease, result);
    }

    private static Result<ProjectLookup<Release>, ProjectError> ProjectFound(Release release) =>
        new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Found(release));

    private void SetupInstallations(string[] releaseNames)
    {
        var installations = releaseNames.Select(CreateInstallation).ToArray();
        _mockInstallationRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));

        foreach (var installation in installations)
        {
            _mockInstallationRegistry.Setup(x => x.FindByReleaseName(installation.ReleaseNameWithRuntime))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));

            _mockInstallationRegistry.Setup(x => x.SetDefault(installation.Key))
                .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));
        }
    }

    private void SetupInstallationsSequence(params string[][] releaseNameSequences)
    {
        var sequence = _mockInstallationRegistry.SetupSequence(x => x.ListInstallations());
        foreach (var releaseNames in releaseNameSequences)
        {
            sequence.Returns(
                new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(
                    releaseNames.Select(CreateInstallation).ToArray()));
        }

        foreach (var releaseName in releaseNameSequences.SelectMany(x => x).Distinct())
        {
            var installation = CreateInstallation(releaseName);
            _mockInstallationRegistry.Setup(x => x.FindByReleaseName(releaseName))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));

            _mockInstallationRegistry.Setup(x => x.SetDefault(installation.Key))
                .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));
        }
    }

    private void SetupReleaseParsing(IEnumerable<string> releaseNames)
    {
        foreach (var releaseName in releaseNames)
        {
            var release = CreateMockRelease(releaseName);
            _mockReleaseManager.Setup(x => x.CreateRelease(releaseName))
                .Returns(ReleaseSuccess(release));
        }
    }

    private static Installation CreateInstallation(string releaseName) =>
        new($"{releaseName}@linux.x86_64", releaseName, "linux.x86_64", releaseName, null, null);

    private static Result<ProjectLookup<Release>, ProjectError> ProjectMissing() =>
        new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());

    private static Result<Release, ReleaseParseError> ReleaseSuccess(Release release) =>
        new Result<Release, ReleaseParseError>.Success(release);

    private static Result<Release, ReleaseParseError> ReleaseFailure(string version) =>
        new Result<Release, ReleaseParseError>.Failure(new ReleaseParseError.InvalidVersion(version));

    private static Result<string, CompatibilityError> CompatibleVersion(string? version) =>
        version is null
            ? new Result<string, CompatibilityError>.Failure(new CompatibilityError.NotFound("test", false))
            : new Result<string, CompatibilityError>.Success(version);

    private static Result<Release, QueryError> QuerySuccess(Release release) =>
        new Result<Release, QueryError>.Success(release);

    private static Result<Release, QueryError> QueryNotFound(string query = "test") =>
        new Result<Release, QueryError>.Failure(new QueryError.NotFound(query));
}
