using System.Security;
using ConsoleAppFramework;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Messages = Fgvm.Cli.Error.Messages;

namespace Fgvm.Cli.Command;

// These are implemented as sub-commands because ConsoleAppFramework doesn't seem to support using flags
// with params, so the options are to either use a named field for args, which isn't user-friendly, or just
// create a dummy sub-command. This means the help menu is not going to capture both under install.
// see: https://github.com/Cysharp/ConsoleAppFramework/issues/179

public sealed class InstallCommand(
    IHostSystem hostSystem,
    IInstallationOrchestrator installationOrchestrator,
    ITemplateOrchestrator templateOrchestrator,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<InstallCommand> logger
)
{
    /// <summary>
    ///     Install a Godot version.
    /// </summary>
    /// <param name="default">-D, Set as the default version after installing</param>
    /// <param name="withTemplates">Install export templates for the selected Godot version after editor installation succeeds.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="ArgumentException">Thrown when the requested version cannot be found or the query is invalid.</exception>
    /// <exception cref="SecurityException">Thrown when checksum verification fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when installation fails for a non-checksum reason.</exception>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    [Command("install|i")]
    public async Task Install(bool @default = false,
        bool withTemplates = false,
        CancellationToken cancellationToken = default,
        [Argument] params string[] query
    ) =>
        await InstallCore(query, @default, withTemplates, cancellationToken);

    /// <summary>
    ///     Core installation logic.
    /// </summary>
    /// <param name="query">Version query arguments</param>
    /// <param name="setAsDefault">Whether to set the installed version as default</param>
    /// <param name="withTemplates">Whether to install export templates after editor installation succeeds.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <exception cref="ArgumentException">Thrown when the requested version cannot be found or the query is invalid.</exception>
    /// <exception cref="SecurityException">Thrown when checksum verification fails.</exception>
    /// <exception cref="InvalidOperationException">Thrown when installation fails for a non-checksum reason.</exception>
    /// <exception cref="OperationCanceledException">Thrown when installation is canceled.</exception>
    private async Task InstallCore(string[] query, bool setAsDefault, bool withTemplates, CancellationToken cancellationToken)
    {
        Result<InstallationOutcome, InstallationError> installationResult;
        try
        {
            installationResult = await installationOrchestrator.InstallAsync(query, setAsDefault, cancellationToken);
            ThrowIfInstallationFailed(installationResult, hostSystem);
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled installation.");
            console.MarkupLine(Messages.UserCancelled("installation"));
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error downloading and installing Godot.");
            console.MarkupLine(
                Messages.SomethingWentWrong($"when trying to install Godot: {e.Message}", pathService)
            );

            throw;
        }

        if (!withTemplates)
        {
            return;
        }

        var releaseNameWithRuntime = GetInstalledReleaseName(installationResult);
        try
        {
            var templateResult = await templateOrchestrator.InstallAsync([releaseNameWithRuntime], false, cancellationToken);
            if (TryGetTemplateInstallationFailureReason(templateResult) is { } failureReason)
            {
                console.MarkupLine(Messages.OptionalTemplateInstallationFailed(releaseNameWithRuntime, failureReason));
            }
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled template installation.");
            console.MarkupLine(Messages.UserCancelled("template installation"));
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error installing export templates for {ReleaseName}.", releaseNameWithRuntime);
            console.MarkupLine(Messages.OptionalTemplateInstallationFailed(releaseNameWithRuntime, e.Message));
        }
    }

    private static void ThrowIfInstallationFailed(Result<InstallationOutcome, InstallationError> installationResult,
        IHostSystem hostSystem
    )
    {
        switch (installationResult)
        {
            case Result<InstallationOutcome, InstallationError>.Success:
                return;

            case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.InvalidQuery invalid):
                throw new ArgumentException(invalid.Message);

            case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.NotFound notFound):
                throw new ArgumentException(Messages.InstallationNotFound(notFound.Version, hostSystem));

            case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.ChecksumMismatch mismatch):
                throw new SecurityException(Messages.ChecksumMismatch(mismatch.FileName, mismatch.Expected, mismatch.Actual));

            case Result<InstallationOutcome, InstallationError>.Failure(InstallationError.Failed failed):
                throw new InvalidOperationException(Messages.InstallationFailed(failed.Reason));

            default:
                throw new Exception(Messages.UnknownInstallationResultType);
        }
    }

    private static string GetInstalledReleaseName(Result<InstallationOutcome, InstallationError> installationResult) =>
        installationResult switch
        {
            Result<InstallationOutcome, InstallationError>.Success(
                InstallationOutcome.NewInstallation(var releaseNameWithRuntime, _, _)) => releaseNameWithRuntime,
            Result<InstallationOutcome, InstallationError>.Success(
                InstallationOutcome.AlreadyInstalled(var releaseNameWithRuntime)) => releaseNameWithRuntime,
            _ => throw new InvalidOperationException(Messages.UnknownInstallationResultType)
        };

    private static string? TryGetTemplateInstallationFailureReason(
        Result<TemplateInstallationOutcome, TemplateInstallationError> templateResult
    )
    {
        return templateResult switch
        {
            Result<TemplateInstallationOutcome, TemplateInstallationError>.Success => null,
            Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.InvalidQuery invalid) =>
                invalid.Message,
            Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.NotFound notFound) =>
                Messages.TemplateInstallationNotFound(notFound.Version),
            Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.ChecksumMismatch mismatch) =>
                Messages.ChecksumMismatch(mismatch.FileName, mismatch.Expected, mismatch.Actual),
            Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(TemplateInstallationError.Failed failed) =>
                Messages.TemplateInstallationFailed(failed.Reason),
            _ => "Unknown template installation result type."
        };
    }
}
