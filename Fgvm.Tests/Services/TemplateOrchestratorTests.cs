using Fgvm.Cli.Services;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Tests.Progress;
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
            new SilentProgressHandler(),
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
        SetupSuccessfulInstall(release, verbose: true);

        var result = await _orchestrator.InstallAsync(query, verbose: true);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        _releaseManager.Verify(x => x.FilterReleasesByQueryWithoutPlatform(query, It.IsAny<string[]>(), false), Times.Once);
        _releaseManager.Verify(x => x.ListReleases(It.IsAny<CancellationToken>()), Times.Never);
        _releaseManager.Verify(x => x.ResolveReleaseQuery(It.IsAny<string[]>(), It.IsAny<string[]>()), Times.Never);
        _releaseManager.Verify(x => x.ResolveReleaseQueryWithoutPlatform(It.IsAny<string[]>(), It.IsAny<string[]>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_ResolvedRelease_ExecutesWithoutReadingInstalledEditors()
    {
        var release = CreateRelease("4.6-stable-standard");
        var progress = new Mock<IOperationProgress<TemplateInstallationStage>>();
        _templateInstallationService.Setup(x => x.InstallAsync(
                release,
                progress.Object,
                false,
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<TemplateInstallationOutcome, TemplateInstallationError>.Success(
                new TemplateInstallationOutcome.NewInstallation(
                    TemplateInstallation.ToTemplateVersion(release),
                    "/templates/4.6.stable",
                    new ChecksumVerification.Verified())));

        var result = await _orchestrator.InstallAsync(
            release,
            progress.Object,
            verbose: true,
            cancellationToken: CancellationToken.None);

        Assert.IsType<Result<TemplateInstallationOutcome, TemplateInstallationError>.Success>(result);
        _installationRegistry.Verify(x => x.ListInstallations(), Times.Never);
        _releaseManager.Verify(
            x => x.FilterReleasesByQueryWithoutPlatform(
                It.IsAny<string[]>(),
                It.IsAny<string[]>(),
                false),
            Times.Never);
        progress.Verify(x => x.Complete("Completed"), Times.Once);

        // Output during a live display corrupts it, so the caller renders
        Assert.Empty(_console.Output);

        _orchestrator.RenderResult(result);
        Assert.Contains("Finished installing export templates", _console.Output);
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
                false,
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
                false,
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
                false,
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
    public async Task SelectForRemovalAsync_WithAmbiguousQueryOnANonInteractiveConsole_Throws()
    {
        var query = new[] { "4.6" };
        var installations = new[]
        {
            new TemplateInstallation("4.6.stable", "4.6-stable-standard", RuntimeEnvironment.Standard, "/templates/4.6.stable", null),
            new TemplateInstallation("4.6.stable.mono", "4.6-stable-mono", RuntimeEnvironment.Mono, "/templates/4.6.stable.mono", null)
        };
        _templateRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(installations));
        _releaseManager.Setup(x => x.FilterReleasesByQueryWithoutPlatform(query, It.IsAny<string[]>(), false))
            .Returns(["4.6-stable-standard", "4.6-stable-mono"]);

        // _console is left non-interactive, which is what a pipe or CI job looks like.
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SelectForRemovalAsync(query));

        Assert.Contains("not interactive", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fgvm template remove", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectForRemovalAsync_ResolvesTemplateWithoutDeletingIt()
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
                It.IsAny<string[]>(),
                false))
            .Returns([installation.ReleaseNameWithRuntime]);

        var result = await _orchestrator.SelectForRemovalAsync(query);

        var success =
            Assert.IsType<Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success>(result);
        Assert.Equal([installation], success.Value);
    }

    private void SetupSuccessfulInstall(Release release, bool verbose = false)
    {
        _templateInstallationService.Setup(x => x.InstallAsync(
                release,
                It.IsAny<IProgress<OperationProgress<TemplateInstallationStage>>>(),
                false,
                verbose,
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
}
