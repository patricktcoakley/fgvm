using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class LocalCommand(IVersionManagementService versionManagementService, IAnsiConsole console, ILogger<LocalCommand> logger)
{
    /// <summary>
    ///     Set the Godot version for the current project.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="ArgumentException">Thrown when a requested version query cannot be resolved during installation.</exception>
    /// <exception cref="InvalidOperationException">Thrown when project information, installed versions, installation state, or `.fgvm-version` writes cannot continue.</exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection or installation is canceled.</exception>
    [Command("local")]
    public async Task Local(CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        try
        {
            var godotRelease = await versionManagementService.SetLocalVersionAsync(query.Length > 0 ? query : null, cancellationToken: cancellationToken);
            console.MarkupLine(Messages.SetLocalVersion(godotRelease.ReleaseNameWithRuntime));
        }
        catch (TaskCanceledException)
        {
            logger.ZLogError($"User cancelled setting local version.");
            console.MarkupLine(Messages.UserCancelled("setting local version"));
            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error setting local version: {e.Message}");
            console.MarkupLine(Messages.SomethingWentWrong("when trying to set the local version"));
            throw;
        }
    }
}
