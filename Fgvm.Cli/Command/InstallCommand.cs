using ConsoleAppFramework;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Security;
using System.Security.Cryptography;
using ZLogger;
using Messages = Fgvm.Cli.Error.Messages;

namespace Fgvm.Cli.Command;

// These are implemented as sub-commands because ConsoleAppFramework doesn't seem to support using flags
// with params, so the options are to either use a named field for args, which isn't user-friendly, or just
// create a dummy sub-command. This means the help menu is not going to capture both under install.
// see: https://github.com/Cysharp/ConsoleAppFramework/issues/179

public sealed class InstallCommand(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IInstallationService installationService,
    IPathService pathService,
    IProgressHandler<InstallationStage> progressHandler,
    IAnsiConsole console,
    ILogger<InstallCommand> logger)
{
    /// <summary>
    ///     Install a Godot version.
    /// </summary>
    /// <param name="default">-D, Set as the default version after installing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="CryptographicException"></exception>
    /// <exception cref="SecurityException"></exception>
    [Command("install|i")]
    public async Task Install(bool @default = false, CancellationToken cancellationToken = default, [Argument] params string[] query) =>
        await InstallCore(query, @default, cancellationToken);

    /// <summary>
    ///     Core installation logic.
    /// </summary>
    /// <param name="query">Version query arguments</param>
    /// <param name="setAsDefault">Whether to set the installed version as default</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="CryptographicException"></exception>
    /// <exception cref="SecurityException"></exception>
    private async Task InstallCore(string[] query, bool setAsDefault, CancellationToken cancellationToken)
    {
        try
        {
            Result<InstallationOutcome, InstallationError> installationResult;
            var wasAutoSetAsDefault = false;

            if (query.Length == 0)
            {
                var releaseNames = await installationService.FetchReleaseNames(cancellationToken);
                var version = await Prompts.Install.ShowVersionSelectionPrompt(releaseNames, console, cancellationToken);
                var godotRelease = releaseManager.TryCreateRelease(version) ?? throw new Exception(Messages.UnableToGetRelease(version));

                // Check if already installed before starting progress
                var existingPath = Path.Combine(pathService.RootPath, godotRelease.ReleaseNameWithRuntime);
                if (Directory.Exists(existingPath))
                {
                    installationResult = new Result<InstallationOutcome, InstallationError>.Success(
                        new InstallationOutcome.AlreadyInstalled(godotRelease.ReleaseNameWithRuntime));
                }
                else
                {
                    // Check if this would be the only version installed
                    var installedVersions = hostSystem.ListInstallations().ToList();
                    var autoSetAsDefault = setAsDefault || installedVersions.Count == 0;
                    wasAutoSetAsDefault = !setAsDefault && installedVersions.Count == 0;

                    installationResult = await progressHandler.TrackProgressAsync(progress =>
                        installationService.InstallReleaseAsync(godotRelease, progress, autoSetAsDefault, cancellationToken));
                }
            }
            else
            {
                // First check without progress to see if we need the progress UI
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
                        // Check if this would be the only version installed
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

                    // Add checksum warning if verification failed
                    if (checksumStatus is ChecksumVerification.Failed(var networkError))
                    {
                        console.MarkupLine(Messages.ChecksumVerificationFailed(release, networkError));
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

                case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.NotFound notFound):
                    throw new InvalidOperationException(Messages.InstallationNotFound(notFound.Version, hostSystem));

                case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.ChecksumMismatch mismatch):
                    throw new SecurityException(Messages.ChecksumMismatch(mismatch.FileName, mismatch.Expected, mismatch.Actual));

                case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.ChecksumParseError parseError):
                    throw new CryptographicException(Messages.ChecksumParseError(parseError.Content));

                case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.Failed failed):
                    throw new InvalidOperationException(Messages.InstallationFailed(failed.Reason));

                default:
                    throw new Exception(Messages.UnknownInstallationResultType);
            }
        }
        catch (TaskCanceledException)
        {
            logger.ZLogError($"User cancelled installation.");
            console.MarkupLine(Messages.UserCancelled("installation"));
            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError(e, $"Error downloading and installing Godot.");
            console.MarkupLine(
                Messages.SomethingWentWrong($"when trying to install Godot: {e.Message}", pathService)
            );

            throw;
        }
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
