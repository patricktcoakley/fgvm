using Fgvm.Cli.Services;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Services;

public sealed class TemplateOrchestratorTests
{
    private readonly TestConsole _console = new();
    private readonly Mock<IInstallationRegistry> _installationRegistry = new();
    private readonly Mock<IReleaseManager> _releaseManager = new();
    private readonly Mock<ITemplateInstallationService> _templateInstallationService = new();
    private readonly Mock<ITemplateRegistry> _templateRegistry = new();
    private readonly TemplateOrchestrator _orchestrator;

    public TemplateOrchestratorTests()
    {
        SetupInstalledGodotVersions([]);

        _orchestrator = new TemplateOrchestrator(
            _releaseManager.Object,
            _installationRegistry.Object,
            _templateRegistry.Object,
            _templateInstallationService.Object,
            new TestProgressHandler<TemplateInstallationStage>(),
            _console);
    }

    [Fact]
    public async Task InstallAsync_WithExplicitQuery_ResolvesAgainstInstalledGodotVersions()
    {
        var query = new[] { "4.6", "mono" };
        var release = CreateRelease("4.6-stable-mono");

        SetupInstalledGodotVersions(["4.6-stable-standard", release.ReleaseNameWithRuntime]);
        _releaseManager.Setup(x => x.FilterReleasesByQueryWithoutPlatform(
                query,
                It.Is<string[]>(releases => releases.SequenceEqual(new[] { "4.6-stable-standard", release.ReleaseNameWithRuntime })),
                false))
            .Returns([release.ReleaseNameWithRuntime]);
        _releaseManager.Setup(x => x.CreateReleaseWithoutPlatform(release.ReleaseNameWithRuntime))
            .Returns(new Result<Release, ReleaseParseError>.Success(release));
        SetupSuccessfulInstall(release);

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        _releaseManager.Verify(x => x.FilterReleasesByQueryWithoutPlatform(query, It.IsAny<string[]>(), false), Times.Once);
        _releaseManager.Verify(x => x.ListReleases(It.IsAny<CancellationToken>()), Times.Never);
        _releaseManager.Verify(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()), Times.Never);
        _releaseManager.Verify(x => x.ResolveReleaseQueryWithoutPlatform(It.IsAny<string[]>(), It.IsAny<string[]>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_WithNoRuntimeInQuery_PrefersInstalledStandardMatch()
    {
        var query = new[] { "4.6" };
        var standard = CreateRelease("4.6-stable-standard");
        var mono = CreateRelease("4.6-stable-mono");

        SetupInstalledGodotVersions([standard.ReleaseNameWithRuntime, mono.ReleaseNameWithRuntime]);
        _releaseManager.Setup(x => x.FilterReleasesByQueryWithoutPlatform(query, It.IsAny<string[]>(), false))
            .Returns([mono.ReleaseNameWithRuntime, standard.ReleaseNameWithRuntime]);
        _releaseManager.Setup(x => x.CreateReleaseWithoutPlatform(standard.ReleaseNameWithRuntime))
            .Returns(new Result<Release, ReleaseParseError>.Success(standard));
        SetupSuccessfulInstall(standard);

        var result = await _orchestrator.InstallAsync(query);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        _templateInstallationService.Verify(
            x => x.InstallAsync(standard, It.IsAny<IProgress<OperationProgress<TemplateInstallationStage>>>(), false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InstallAsync_WithNoQuery_PromptsFromInstalledGodotVersions()
    {
        var release = CreateRelease("4.5-stable-standard");
        SetupInstalledGodotVersions([release.ReleaseNameWithRuntime]);
        _releaseManager.Setup(x => x.CreateReleaseWithoutPlatform(release.ReleaseNameWithRuntime))
            .Returns(new Result<Release, ReleaseParseError>.Success(release));
        SetupSuccessfulInstall(release);
        _console.Interactive();
        _console.Input.PushKey(ConsoleKey.Enter);

        var result = await _orchestrator.InstallAsync([]);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        _templateInstallationService.Verify(
            x => x.InstallAsync(release, It.IsAny<IProgress<OperationProgress<TemplateInstallationStage>>>(), false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InstallAsync_WithExplicitQueryThatMatchesNoInstalledVersion_ReturnsNotFound()
    {
        var query = new[] { "4.6" };
        SetupInstalledGodotVersions(["4.5-stable-standard"]);
        _releaseManager.Setup(x => x.FilterReleasesByQueryWithoutPlatform(query, It.IsAny<string[]>(), false))
            .Returns([]);

        var result = await _orchestrator.InstallAsync(query);

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        Assert.IsType<TemplateInstallationError.NotFound>(failure.Error);
        _templateInstallationService.Verify(
            x => x.InstallAsync(It.IsAny<Release>(), It.IsAny<IProgress<OperationProgress<TemplateInstallationStage>>>(), false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InstallAsync_WithNoInstalledGodotVersions_ReturnsFailure()
    {
        SetupInstalledGodotVersions([]);

        var result = await _orchestrator.InstallAsync(["4.6"]);

        var failure = Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure>(result);
        var failed = Assert.IsType<TemplateInstallationError.Failed>(failure.Error);
        Assert.Contains("No installed Godot versions", failed.Reason);
    }

    [Fact]
    public async Task RemoveAsync_WithExactQuery_RemovesMatchingTemplateDirectory()
    {
        var query = new[] { "4.6" };
        var installation = new TemplateInstallation(
            "4.6.stable",
            "4.6-stable-standard",
            RuntimeEnvironment.Standard,
            "/templates/4.6.stable",
            null);

        _templateRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([installation]));
        _releaseManager.Setup(x => x.FilterReleasesByQueryWithoutPlatform(
                query,
                It.Is<string[]>(releases => releases.SequenceEqual(new[] { installation.ReleaseNameWithRuntime })),
                false))
            .Returns([installation.ReleaseNameWithRuntime]);
        _templateRegistry.Setup(x => x.Remove(installation.TemplateVersion))
            .Returns(new Result<Unit, TemplateRegistryError>.Success(Unit.Value));

        var result = await _orchestrator.RemoveAsync(query);

        Assert.IsType<Result<Unit, TemplateRegistryError>.Success>(result);
        _templateRegistry.Verify(x => x.Remove(installation.TemplateVersion), Times.Once);
    }

    private void SetupSuccessfulInstall(Release release)
    {
        _templateInstallationService.Setup(x => x.InstallAsync(
                release,
                It.IsAny<IProgress<OperationProgress<TemplateInstallationStage>>>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.NewInstallation(
                    TemplateInstallation.ToTemplateVersion(release),
                    $"/templates/{TemplateInstallation.ToTemplateVersion(release)}",
                    new ChecksumVerification.Verified())));
    }

    private void SetupInstalledGodotVersions(string[] releaseNames)
    {
        var installations = releaseNames
            .Select(name => new Installation(
                $"{name}@linux.x86_64",
                name,
                "linux.x86_64",
                $"installations/{name}/linux.x86_64",
                null,
                null))
            .ToArray();

        _installationRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));
    }

    private static Release CreateRelease(string releaseNameWithRuntime)
    {
        var release = Release.TryParse(releaseNameWithRuntime);
        Assert.NotNull(release);
        return release;
    }

    private sealed class TestProgressHandler<TStage> : IProgressHandler<TStage> where TStage : Enum
    {
        public Task<T> TrackProgressAsync<T>(Func<IProgress<OperationProgress<TStage>>, Task<T>> operation) =>
            operation(new Progress<OperationProgress<TStage>>(_ => { }));
    }
}
