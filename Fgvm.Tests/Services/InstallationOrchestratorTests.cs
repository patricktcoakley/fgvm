using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Services;

public sealed class InstallationOrchestratorTests : IDisposable
{
    private readonly TestConsole _console;
    private readonly Mock<IHostSystem> _mockHostSystem;
    private readonly Mock<IInstallationRegistry> _mockInstallationRegistry;
    private readonly Mock<IInstallationService> _mockInstallationService;
    private readonly Mock<IReleaseManager> _mockReleaseManager;
    private readonly InstallationOrchestrator _orchestrator;
    private readonly string _tempRoot;

    public InstallationOrchestratorTests()
    {
        _console = new TestConsole();
        _mockHostSystem = new Mock<IHostSystem>();
        _mockInstallationRegistry = new Mock<IInstallationRegistry>();
        _mockInstallationService = new Mock<IInstallationService>();
        _mockReleaseManager = new Mock<IReleaseManager>();
        var mockPathService = new Mock<IPathService>();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"fgvm-orchestrator-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRoot);

        mockPathService.Setup(x => x.RootPath).Returns(_tempRoot);
        mockPathService.Setup(x => x.SymlinkPath).Returns(Path.Combine(_tempRoot, "Godot"));
        mockPathService.Setup(x => x.ConfigPath).Returns(Path.Combine(_tempRoot, "fgvm.ini"));
        mockPathService.Setup(x => x.ReleasesPath).Returns(Path.Combine(_tempRoot, "releases.json"));
        mockPathService.Setup(x => x.BinPath).Returns(Path.Combine(_tempRoot, "bin"));
        mockPathService.Setup(x => x.MacAppSymlinkPath).Returns(Path.Combine(_tempRoot, "Godot.app"));
        mockPathService.Setup(x => x.LogPath).Returns(Path.Combine(_tempRoot, "fgvm.log"));

        _mockInstallationService.Setup(x => x.FetchReleaseNames(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()))
            .Returns(new Result<Release, QueryError>.Failure(new QueryError.NotFound("test")));

        SetupInstallations([]);

        _orchestrator = new InstallationOrchestrator(
            _mockReleaseManager.Object,
            _mockInstallationRegistry.Object,
            _mockInstallationService.Object,
            new TestProgressHandler<InstallationStage>(),
            _console);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    [Fact]
    public async Task InstallAsync_InstalledRelease_ReturnsAlreadyInstalledAndSkipsInstall()
    {
        var query = new[] { "4.3.0" };
        var releaseNames = new[] { "4.3.0-stable" };
        var release = Release.TryParse("4.3.0-stable-standard")!;

        _mockInstallationService.Setup(x => x.FetchReleaseNames(It.IsAny<CancellationToken>()))
            .ReturnsAsync(releaseNames);

        _mockReleaseManager.Setup(x => x.ResolveReleaseQuery(query, releaseNames))
            .Returns(new Result<Release, QueryError>.Success(release));

        SetupInstallations([release.ReleaseNameWithRuntime]);

        var result = await _orchestrator.InstallAsync(query);

        var success = Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        var alreadyInstalled = Assert.IsType<InstallationOutcome.AlreadyInstalled>(success.Value);
        Assert.Equal(release.ReleaseNameWithRuntime, alreadyInstalled.ReleaseNameWithRuntime);
        Assert.Contains("already installed", _console.Output, StringComparison.OrdinalIgnoreCase);

        _mockInstallationService.Verify(
            x => x.InstallByQueryAsync(It.IsAny<string[]>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _mockInstallationService.Verify(
            x => x.InstallReleaseAsync(It.IsAny<Release>(), It.IsAny<IProgress<OperationProgress<InstallationStage>>>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InstallAsync_QueryInstall_RendersSuccessOutput()
    {
        var query = new[] { "4.3.0" };
        const string releaseName = "4.3.0-stable";

        SetupInstallations(["4.2.0-stable"]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(
                    It.Is<string[]>(q => q.SequenceEqual(query)),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation(releaseName, new ChecksumVerification.Verified())));

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        Assert.Contains("Finished installing 4.3.0-stable", _console.Output);
    }

    [Fact]
    public async Task InstallAsync_NoInstalledVersions_AutoSetsDefault()
    {
        var query = new[] { "4.3.0" };

        SetupInstallations([]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(
                    It.Is<string[]>(q => q.SequenceEqual(query)),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                    true,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation("4.3.0-stable", new ChecksumVerification.Verified())));

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        Assert.Contains("Set as default version since no other versions are installed", _console.Output);
    }

    [Fact]
    public async Task InstallAsync_SetDefaultTrue_RendersDefaultNote()
    {
        var query = new[] { "4.3.0" };

        SetupInstallations(["4.2.0-stable"]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(
                    It.Is<string[]>(q => q.SequenceEqual(query)),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                    true,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation("4.3.0-stable", new ChecksumVerification.Verified())));

        var result = await _orchestrator.InstallAsync(query, true);

        Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        Assert.Contains("Set as default version", _console.Output);
    }

    [Fact]
    public async Task InstallAsync_SymlinkWarning_RendersWarning()
    {
        var query = new[] { "4.3.0" };

        SetupInstallations(["4.2.0-stable"]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(
                    It.Is<string[]>(q => q.SequenceEqual(query)),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation("4.3.0-stable", new ChecksumVerification.Verified(),
                    new SymlinkError.PermissionDenied())));

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        Assert.Contains("Unable to create symlinks due to insufficient permissions", _console.Output);
    }

    [Fact]
    public async Task InstallAsync_UnavailableChecksum_RendersExplicitWarning()
    {
        var query = new[] { "3.2.1" };

        SetupInstallations(["4.2.0-stable"]);
        _mockInstallationService.Setup(x =>
                x.InstallByQueryAsync(
                    It.Is<string[]>(q => q.SequenceEqual(query)),
                    It.IsAny<IProgress<OperationProgress<InstallationStage>>>(),
                    false,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<InstallationOutcome, InstallationError>.Success(
                new InstallationOutcome.NewInstallation("3.2.1-stable-standard", new ChecksumVerification.Unavailable())));

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<InstallationOutcome, InstallationError>.Success>(result);
        Assert.Contains("Checksum unavailable", _console.Output);
        Assert.Contains("verification", _console.Output);
    }

    private void SetupInstallations(string[] releaseNames)
    {
        var installations = releaseNames
            .Select(name => new Installation($"{name}@linux.x86_64", name, "linux.x86_64", name, null, null))
            .ToArray();

        _mockInstallationRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));

        foreach (var installation in installations)
        {
            _mockInstallationRegistry.Setup(x => x.FindByReleaseName(installation.ReleaseNameWithRuntime))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));
        }
    }

    private sealed class TestProgressHandler<TStage> : IProgressHandler<TStage> where TStage : Enum
    {
        public Task<T> TrackProgressAsync<T>(Func<IProgress<OperationProgress<TStage>>, Task<T>> operation) =>
            operation(new Progress<OperationProgress<TStage>>(_ => { }));
    }
}
