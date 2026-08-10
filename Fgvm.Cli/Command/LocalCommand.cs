using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;


namespace Fgvm.Cli.Command;

public sealed class LocalCommand(
    IVersionManagementService versionManagementService,
    IExportPresetCatalog exportPresetCatalog,
    ITemplateOrchestrator templateOrchestrator,
    IAnsiConsole console,
    ILogger<LocalCommand> logger
)
{
    /// <summary>
    ///     Prepare the Godot editor and export templates for the current project.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="ArgumentException">Thrown when a requested version query cannot be resolved during installation.</exception>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when project information, export presets, editor/template installation state, or
    ///     `.fgvm-version` writes cannot continue.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection or installation is canceled.</exception>
    [Command("local")]
    public async Task Local(CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        Release? godotRelease = null;
        var templatesRequired = false;

        try
        {
            var exportPresets = ReadExportPresets();
            templatesRequired = exportPresets.Any(preset => preset.IsConfigured);
            godotRelease =
                await versionManagementService.SetLocalVersionAsync(query.Length > 0 ? query : null, cancellationToken: cancellationToken);

            if (templatesRequired)
            {
                Result<TemplateInstallationOutcome, TemplateInstallationError> templateResult;
                try
                {
                    templateResult = await templateOrchestrator.InstallAsync(
                        [godotRelease.ReleaseNameWithRuntime],
                        cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "Local version {Release} was set, but export template installation failed: {Message}",
                        godotRelease.ReleaseNameWithRuntime,
                        exception.Message);
                    console.MarkupLine(Messages.LocalTemplateInstallationFailed(
                        godotRelease.ReleaseNameWithRuntime,
                        exception.Message));
                    throw new ProcessExitCodeException(ExitCodes.GeneralError);
                }

                if (templateResult is Result<TemplateInstallationOutcome, TemplateInstallationError>.Failure(var error))
                {
                    var reason = TemplateInstallationResult.DescribeError(error);
                    logger.LogError(
                        "Local version {Release} was set, but export template installation failed: {Reason}",
                        godotRelease.ReleaseNameWithRuntime,
                        reason);
                    console.MarkupLine(Messages.LocalTemplateInstallationFailed(godotRelease.ReleaseNameWithRuntime, reason));
                    throw new ProcessExitCodeException(ExitCodes.GeneralError);
                }
            }

            console.MarkupLine(Messages.SetLocalVersion(godotRelease.ReleaseNameWithRuntime));
        }
        catch (OperationCanceledException) when (godotRelease is not null && templatesRequired)
        {
            logger.LogError(
                "Local version {Release} was set, but export template installation was cancelled.",
                godotRelease.ReleaseNameWithRuntime);
            console.MarkupLine(Messages.LocalTemplateInstallationCancelled(godotRelease.ReleaseNameWithRuntime));
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("User cancelled setting local version.");
            console.MarkupLine(Messages.UserCancelled("setting local version"));
            throw;
        }
        catch (ConfigurationException)
        {
            throw;
        }
        catch (ProcessExitCodeException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error setting local version: {Message}", e.Message);
            console.MarkupLine(Messages.SomethingWentWrong("when trying to set the local version"));
            throw;
        }
    }

    private IReadOnlyList<ExportPreset> ReadExportPresets() =>
        exportPresetCatalog.Read() switch
        {
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(var presets) => presets,
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                ExportPresetCatalogError.InvalidConfiguration(var path, var line, var message)) =>
                throw new ConfigurationException($"Invalid export preset configuration in '{path}' at line {line}: {message}"),
            Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                ExportPresetCatalogError.FileAccess(var error)) =>
                throw new ConfigurationException($"Unable to read export presets: {error}"),
            _ => throw new InvalidOperationException("Unexpected export preset catalog result type.")
        };
}
