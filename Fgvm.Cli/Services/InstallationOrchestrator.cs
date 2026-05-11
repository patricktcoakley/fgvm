using Fgvm.Cli.Error;
using Fgvm.Cli.Prompts;
using Fgvm.Environment;
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
    Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query, bool setAsDefault = false,
        CancellationToken cancellationToken = default);
}

public sealed class InstallationOrchestrator(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IInstallationService installationService,
    IPathService pathService,
    IProgressHandler<InstallationStage> progressHandler,
    IAnsiConsole console) : IInstallationOrchestrator
{
    public async Task<Result<InstallationOutcome, InstallationError>> InstallAsync(string[] query, bool setAsDefault = false,
        CancellationToken cancellationToken = default)
    {
        Result<InstallationOutcome, InstallationError> installationResult;
        var wasAutoSetAsDefault = false;

        if (query.Length == 0)
        {
            var releaseNames = await installationService.FetchReleaseNames(cancellationToken);
            var version = await Install.ShowVersionSelectionPrompt(releaseNames, console, cancellationToken);
            var godotRelease = releaseManager.TryCreateRelease(version) ?? throw new Exception(Messages.UnableToGetRelease(version));

            var existingPath = Path.Combine(pathService.RootPath, godotRelease.ReleaseNameWithRuntime);
            if (Directory.Exists(existingPath))
            {
                installationResult = new Result<InstallationOutcome, InstallationError>.Success(
                    new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
            }
            else
            {
                var installedVersions = hostSystem.ListInstallations().ToList();
                var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                installationResult = await progressHandler.TrackProgressAsync(progress =>
                    installationService.InstallReleaseAsync(godotRelease, progress, autoSetAsDefault, cancellationToken));
            }
        }
        else
        {
            var releaseNames = await installationService.FetchReleaseNames(cancellationToken);
            var godotRelease = releaseManager.TryFindReleaseByQuery(query, releaseNames);

            if (godotRelease != null)
            {
                var existingPath = Path.Combine(pathService.RootPath, godotRelease.ReleaseNameWithRuntime);
                if (Directory.Exists(existingPath))
                {
                    installationResult = new Result<InstallationOutcome, InstallationError>.Success(
                        new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
                }
                else
                {
                    var installedVersions = hostSystem.ListInstallations().ToList();
                    var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                    wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                    installationResult = await progressHandler.TrackProgressAsync(progress =>
                        installationService.InstallByQueryAsync(query, progress, autoSetAsDefault, cancellationToken));
                }
            }
            else
            {
                var installedVersions = hostSystem.ListInstallations().ToList();
                var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                installationResult = await progressHandler.TrackProgressAsync(progress =>
                    installationService.InstallByQueryAsync(query, progress, autoSetAsDefault, cancellationToken));
            }
        }

        switch (installationResult)
        {
            case Result<InstallationOutcome, InstallationError>.Success(InstallationOutcome.NewInstallation(var release, var checksumStatus, var symlinkWarning)):
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
                        case SymlinkError.DeveloperModeRequired:
                            console.MarkupLine(Messages.DeveloperModeRequiredForSymlink);
                            break;
                        case SymlinkError.PermissionDenied:
                            console.MarkupLine(Messages.SymlinkPermissionDenied);
                            break;
                        case SymlinkError.UnsupportedOS(var os):
                            console.MarkupLine(Messages.SymlinkUnsupportedOS(os));
                            break;
                        case SymlinkError.InvalidSymlink(var path, _):
                            console.MarkupLine(Messages.InvalidSymlinkWarn(path));
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

    private static string GetInstallationSuccessMessage(string releaseNameWithRuntime, bool setAsDefault, bool wasAutoSetAsDefault)
    {
        var baseMessage = Messages.InstallationSuccessBase(releaseNameWithRuntime);

        if (wasAutoSetAsDefault)
        {
            return $"{baseMessage}\n{Messages.AutoSetAsDefaultNote}";
        }

        return setAsDefault ? $"{baseMessage}\n{Messages.SetAsDefaultVersionNote}" : baseMessage;
    }
}
