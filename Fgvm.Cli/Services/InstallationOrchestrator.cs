using Fgvm.Cli.Error;
using Fgvm.Cli.Prompts;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Spectre.Console;

namespace Fgvm.Cli.Services;

/// <summary>
///     CLI orchestration layer for installation UX and policy, shared by both `fgvm install`
///     and project auto-install in `fgvm godot`. Core install mechanics remain in
///     <see cref="IInstallationService" />. This layer always renders CLI output.
/// </summary>
public interface IInstallationOrchestrator
{
    /// <summary>
    ///     Installs a Godot release from a query or interactive selection.
    /// </summary>
    /// <param name="query">Version query arguments.</param>
    /// <param name="setAsDefault">Whether to set the installed version as default.</param>
    /// <param name="verbose">Whether to show each download source as it is tried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation result.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when release names, installed versions, or release parsing cannot
    ///     continue.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection or installation is canceled.</exception>
    Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query,
        bool setAsDefault = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Installs an already-resolved release using a caller-owned progress operation. Writes nothing to the console;
    ///     the caller renders with <see cref="RenderResult" /> once the progress session has ended.
    /// </summary>
    /// <param name="release">The release to install.</param>
    /// <param name="progress">The progress operation to drive.</param>
    /// <param name="setAsDefault">Whether to set the installed version as default.</param>
    /// <param name="verbose">Whether to report each download source as it is tried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InstallationAttempt> InstallAsync(Release release,
        IOperationProgress<InstallationStage> progress,
        bool setAsDefault = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Renders the outcome of an installation. Call only outside a progress session.
    /// </summary>
    /// <param name="attempt">The installation to render.</param>
    /// <param name="setAsDefault">Whether the caller asked for the version to become the default.</param>
    void RenderResult(InstallationAttempt attempt, bool setAsDefault);
}

/// <summary>
///     An installation result plus the policy decision that only the installing call knows about.
/// </summary>
/// <param name="Result">The installation result.</param>
/// <param name="WasAutoSetAsDefault">Whether it became the default because it was the only version installed.</param>
public sealed record InstallationAttempt(
    Result<InstallationOutcome, InstallationError> Result,
    bool WasAutoSetAsDefault
);

public sealed class InstallationOrchestrator(
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    IInstallationService installationService,
    IProgressHandler progressHandler,
    IAnsiConsole console
) : IInstallationOrchestrator
{
    /// <inheritdoc />
    public async Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query,
        bool setAsDefault = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    )
    {
        Result<InstallationOutcome, InstallationError> installationResult;
        var wasAutoSetAsDefault = false;

        if (query.Length == 0)
        {
            if (!console.Profile.Capabilities.Interactive)
            {
                throw new ArgumentException(Messages.VersionQueryRequiredInNonInteractiveShell("fgvm install"));
            }

            var releaseNames = await FetchReleaseNames(cancellationToken);
            var version = await Install.ShowVersionSelectionPrompt(releaseNames, console, cancellationToken);
            var godotRelease = CreateRelease(version);
            return await TrackResolvedRelease(godotRelease, setAsDefault, verbose, cancellationToken);
        }

        var availableReleases = await FetchReleaseNames(cancellationToken);
        switch (releaseManager.ResolveReleaseQuery(query, availableReleases))
        {
            case Result<Release, QueryError>.Success(var godotRelease):
                return await TrackResolvedRelease(godotRelease, setAsDefault, verbose, cancellationToken);

            case Result<Release, QueryError>.Failure(QueryError.NotFound):
                var installedVersions = ListInstallations();
                var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;
                installationResult = await TrackQueryInstallation(
                    query, autoSetAsDefault, verbose, cancellationToken);
                break;

            case Result<Release, QueryError>.Failure(var error):
                installationResult = new Result<InstallationOutcome, InstallationError>.Failure(
                    MapQueryError(error, query));
                break;

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        RenderResult(new InstallationAttempt(installationResult, wasAutoSetAsDefault), setAsDefault);
        return installationResult;
    }

    /// <inheritdoc />
    public async Task<InstallationAttempt> InstallAsync(Release release,
        IOperationProgress<InstallationStage> progress,
        bool setAsDefault = false,
        bool verbose = false,
        CancellationToken cancellationToken = default
    )
    {
        var wasAutoSetAsDefault = false;
        var installationResult = await progress.RunAsync(
            async () =>
            {
                if (IsInstalled(release.ReleaseNameWithRuntime))
                {
                    return new Result<InstallationOutcome, InstallationError>.Success(
                        new InstallationOutcome.AlreadyInstalled(release.ReleaseNameWithRuntime));
                }

                var installedVersions = ListInstallations();
                var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;
                return await installationService.InstallReleaseAsync(
                    release, progress, autoSetAsDefault, verbose, cancellationToken);
            },
            result => result is Result<InstallationOutcome, InstallationError>.Success);

        return new InstallationAttempt(installationResult, wasAutoSetAsDefault);
    }

    private async Task<Result<InstallationOutcome, InstallationError>> TrackResolvedRelease(Release release,
        bool setAsDefault,
        bool verbose,
        CancellationToken cancellationToken
    )
    {
        var attempt = await progressHandler.TrackProgressAsync(session =>
            InstallAsync(
                release,
                session.AddOperation<InstallationStage>("Editor"),
                setAsDefault,
                verbose,
                cancellationToken));

        RenderResult(attempt, setAsDefault);
        return attempt.Result;
    }

    private async Task<Result<InstallationOutcome, InstallationError>> TrackQueryInstallation(string[] query,
        bool setAsDefault,
        bool verbose,
        CancellationToken cancellationToken
    ) =>
        await progressHandler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            return await progress.RunAsync(
                async () => await installationService.InstallByQueryAsync(
                    query, progress, setAsDefault, verbose, cancellationToken),
                result => result is Result<InstallationOutcome, InstallationError>.Success);
        });

    /// <inheritdoc />
    public void RenderResult(InstallationAttempt attempt, bool setAsDefault)
    {
        var (installationResult, wasAutoSetAsDefault) = attempt;
        switch (installationResult)
        {
            case Result<InstallationOutcome, InstallationError>.Success(
                InstallationOutcome.NewInstallation(var release, var checksumStatus, var symlinkWarning)):
                var successMessage = GetInstallationSuccessMessage(release, setAsDefault, wasAutoSetAsDefault);
                console.MarkupLine(successMessage);

                if (checksumStatus is ChecksumVerification.Unavailable)
                {
                    console.MarkupLine(Messages.ChecksumUnavailable(release));
                }

                if (symlinkWarning is not null)
                {
                    switch (symlinkWarning)
                    {
                        case SymlinkError.PermissionDenied:
                            console.MarkupLine(Messages.SymlinkPermissionDenied);
                            break;
                        case SymlinkError.UnsupportedOS(var os):
                            console.MarkupLine(Messages.SymlinkUnsupportedOS(os));
                            break;
                        case SymlinkError.InvalidSymlink(var path, _):
                            console.MarkupLine(Messages.InvalidSymlinkWarn(path));
                            break;
                        case SymlinkError.RemoveFailed(var removePath):
                            console.MarkupLine(Messages.SymlinkUpdateFailed(removePath));
                            break;
                    }
                }

                break;

            case Result<InstallationOutcome, InstallationError>.Success(InstallationOutcome.AlreadyInstalled(var release)):
                console.MarkupLine(Messages.AlreadyInstalled(release));
                break;
        }
    }

    private async Task<string[]> FetchReleaseNames(CancellationToken cancellationToken)
    {
        return await installationService.FetchReleaseNames(cancellationToken) switch
        {
            Result<string[], NetworkError>.Success(var releaseNames) => releaseNames,
            Result<string[], NetworkError>.Failure(NetworkError.ManifestRefreshFailure(var releaseNames)) =>
                UseCachedReleaseNames(releaseNames),
            Result<string[], NetworkError>.Failure =>
                throw new InvalidOperationException("Unable to fetch available Godot releases."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private string[] UseCachedReleaseNames(IEnumerable<string> releaseNames)
    {
        console.MarkupLine(Messages.ReleaseCacheRefreshFailed);
        return releaseNames.ToArray();
    }

    private IReadOnlyList<string> ListInstallations() =>
        installationRegistry.ListInstallations() switch
        {
            Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(var installations) =>
                installations.Select(installation => installation.ReleaseNameWithRuntime).ToArray(),
            Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure =>
                throw new InvalidOperationException("Unable to read installed Godot versions."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private bool IsInstalled(string releaseNameWithRuntime) =>
        installationRegistry.FindByReleaseName(releaseNameWithRuntime) is Result<Installation, InstallationRegistryError>.Success;

    private Release CreateRelease(string version) =>
        releaseManager.CreateRelease(version) switch
        {
            Result<Release, ReleaseParseError>.Success(var release) => release,
            Result<Release, ReleaseParseError>.Failure => throw new InvalidOperationException(Messages.UnableToGetRelease(version)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private static string GetInstallationSuccessMessage(string releaseNameWithRuntime, bool setAsDefault, bool wasAutoSetAsDefault)
    {
        var baseMessage = Messages.InstallationSuccessBase(releaseNameWithRuntime);

        if (wasAutoSetAsDefault)
        {
            return $"{baseMessage}\n{Messages.AutoSetAsDefaultNote}";
        }

        return setAsDefault ? $"{baseMessage}\n{Messages.SetAsDefaultVersionNote}" : baseMessage;
    }

    private static InstallationError MapQueryError(QueryError error, string[] query) => error switch
    {
        QueryError.EmptyQuery => new InstallationError.InvalidQuery("Version query is required."),
        QueryError.InvalidQuery invalid => new InstallationError.InvalidQuery(invalid.Message),
        QueryError.NotFound notFound => new InstallationError.NotFound(notFound.Query),
        _ => new InstallationError.NotFound(string.Join(" ", query))
    };
}
