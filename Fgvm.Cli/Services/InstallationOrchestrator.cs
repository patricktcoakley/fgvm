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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation result.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when release names, installed versions, or release parsing cannot
    ///     continue.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection or installation is canceled.</exception>
    Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query,
        bool setAsDefault = false,
        CancellationToken cancellationToken = default
    );
}

public sealed class InstallationOrchestrator(
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    IInstallationService installationService,
    IProgressHandler<InstallationStage> progressHandler,
    IAnsiConsole console
) : IInstallationOrchestrator
{
    /// <inheritdoc />
    public async Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query,
        bool setAsDefault = false,
        CancellationToken cancellationToken = default
    )
    {
        Result<InstallationOutcome, InstallationError> installationResult;
        var wasAutoSetAsDefault = false;

        if (query.Length == 0)
        {
            var releaseNames = await FetchReleaseNames(cancellationToken);
            var version = await Install.ShowVersionSelectionPrompt(releaseNames, console, cancellationToken);
            var godotRelease = CreateRelease(version);

            if (IsInstalled(godotRelease.ReleaseNameWithRuntime))
            {
                installationResult = new Result<InstallationOutcome, InstallationError>.Success(
                    new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
            }
            else
            {
                var installedVersions = ListInstallations();
                var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                installationResult = await progressHandler.TrackProgressAsync(progress =>
                    installationService.InstallReleaseAsync(godotRelease, progress, autoSetAsDefault, cancellationToken));
            }
        }
        else
        {
            var releaseNames = await FetchReleaseNames(cancellationToken);
            switch (releaseManager.ResolveReleaseQuery(query, releaseNames))
            {
                case Result<Release, QueryError>.Success(var godotRelease):
                {
                    if (IsInstalled(godotRelease.ReleaseNameWithRuntime))
                    {
                        installationResult = new Result<InstallationOutcome, InstallationError>.Success(
                            new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
                    }
                    else
                    {
                        var installedVersions = ListInstallations();
                        var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                        wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                        installationResult = await progressHandler.TrackProgressAsync(progress =>
                            installationService.InstallReleaseAsync(godotRelease, progress, autoSetAsDefault, cancellationToken));
                    }

                    break;
                }

                case Result<Release, QueryError>.Failure(QueryError.NotFound):
                {
                    var installedVersions = ListInstallations();
                    var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                    wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                    installationResult = await progressHandler.TrackProgressAsync(progress =>
                        installationService.InstallByQueryAsync(query, progress, autoSetAsDefault, cancellationToken));

                    break;
                }

                case Result<Release, QueryError>.Failure(var error):
                    installationResult = new Result<InstallationOutcome, InstallationError>.Failure(MapQueryError(error, query));
                    break;

                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }

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

        return installationResult;
    }

    private async Task<string[]> FetchReleaseNames(CancellationToken cancellationToken)
    {
        return await installationService.FetchReleaseNames(cancellationToken) switch
        {
            Result<string[], NetworkError>.Success(var releaseNames) => releaseNames,
            Result<string[], NetworkError>.Failure =>
                throw new InvalidOperationException("Unable to fetch available Godot releases."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
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
