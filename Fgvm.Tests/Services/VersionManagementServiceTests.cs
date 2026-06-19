using System.Runtime.InteropServices;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console.Testing;
using RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Services;

public class VersionManagementServiceTests
{
    private readonly TestConsole _console;
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
        var installFlowProgressHandler = new TestProgressHandler<InstallationStage>();

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
            mockLogger.Object
        );
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithNoProjectAndNoInstallations_ReturnsNotFound()
    {
        SetupInstallations([]);

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Failure { Error: VersionResolutionError.NotFound });
        var hasProjectMessage = _console.Output.Contains("Project requires") || _console.Output.Contains("Project specifies");
        var hasNoInstallationMessage = _console.Output.Contains("No Godot versions installed");
        var hasNoVersionSetMessage = _console.Output.Contains("No current Godot version set");
        var hasNotFoundMessage = _console.Output.Contains("could not be found");

        Assert.True(hasProjectMessage || hasNoInstallationMessage || hasNoVersionSetMessage || hasNotFoundMessage,
            $"Expected either project-specific, no installations, no version set, or not found message. Actual output: {_console.Output}");
    }

    [Fact]
    public async Task ResolveVersionForLaunchAsync_WithProjectVersion_ReturnsCorrectResult()
    {
        const string projectVersion = "4.3.0";
        const string compatibleVersion = "4.3.0-stable-standard";
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
                x.FindCompatibleVersionResult(projectRelease.ReleaseNameWithRuntime, false, installedVersions))
            .Returns(compatibleVersion);

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
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectRelease.ReleaseNameWithRuntime, false, installedVersions))
            .Returns(CompatibleVersion(null));

        var result = await _service.ResolveVersionForLaunchAsync();

        Assert.True(result is Result<VersionResolutionOutcome, VersionResolutionError>.Failure { Error: VersionResolutionError.NotFound });
        Assert.True(_console.Output.Contains("Project requires") || _console.Output.Contains("could not be found"),
            $"Expected project or not found message. Actual: {_console.Output}");
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
                x.FindCompatibleVersionResult(projectRelease.ReleaseNameWithRuntime, false, installedVersions))
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
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(localVersion,
                false,
                It.Is<IEnumerable<string>>(versions => versions.SequenceEqual(installedVersions))))
            .Returns(localVersion);
        _mockReleaseManager.Setup(x => x.CreateRelease(localVersion))
            .Returns(CreateMockRelease(localVersion));

        var result = await _service.ResolveEffectiveVersionAsync();

        var success = Assert.IsType<Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success>(result);
        Assert.Equal(localVersion, success.Value.VersionName);
        Assert.True(success.Value.IsProjectVersion);
        Assert.Contains(localVersion, success.Value.ExecutablePath);
        Assert.Empty(_console.Output);
        _mockInstallationRegistry.Verify(x => x.GetDefault(), Times.Never);
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
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void CreateOrUpdateVersionFile_CallsProjectManager()
    {
        const string version = "4.3.0-stable";
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetLocalVersionAsync(forceInteractive: true));

        Assert.Empty(_console.Output);
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
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains("No installed version found matching", _console.Output);
        Assert.Contains("Installing", _console.Output);
        Assert.Contains("Successfully installed", _console.Output);
        Assert.Contains("`.fgvm-version` file in current directory", _console.Output);
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
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(installationResult);

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false);

        var success = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Success>(result);
        var installed = Assert.IsType<CompatibleVersionOutcome.Installed>(success.Value);
        Assert.Equal(compatibleVersion, installed.Version);
        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.Is<string[]>(q => Enumerable.SequenceEqual(q, new[] { projectVersion })),
                It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
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
        _mockReleaseManager.Setup(x => x.FindCompatibleVersionResult(projectVersion, false, It.IsAny<IEnumerable<string>>()))
            .Returns(CompatibleVersion(null));

        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(installedVersion, new ChecksumVerification.Verified())));

        var result = await _service.FindOrInstallCompatibleVersionAsync(projectVersion, false, false);

        var failure = Assert.IsType<Result<CompatibleVersionOutcome, CompatibleVersionError>.Failure>(result);
        var resolutionFailed = Assert.IsType<CompatibleVersionError.ResolutionFailed>(failure.Error);
        Assert.Equal(projectVersion, resolutionFailed.ProjectVersion);
        Assert.Equal(installedVersion, resolutionFailed.InstalledVersion);
    }

    [Fact]
    public async Task SetGlobalVersionAsync_WithNoInstallations_ThrowsException()
    {
        SetupInstallations([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetGlobalVersionAsync(["4.3.0"]));

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetGlobalVersionAsync(query));

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
        Assert.Contains("Error resolving Godot version for launch", _console.Output);
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
                    It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(errorMessage));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetLocalVersionAsync(query));

        Assert.Contains(errorMessage, exception.Message);
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

    private sealed class TestProgressHandler<TStage> : IProgressHandler<TStage> where TStage : Enum
    {
        public Task<T> TrackProgressAsync<T>(Func<IProgress<OperationProgress<TStage>>, Task<T>> operation) =>
            operation(new Progress<OperationProgress<TStage>>(_ => { }));
    }
}
