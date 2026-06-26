using ConsoleAppFramework;
using Fgvm.Cli.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Messages = Fgvm.Cli.Error.Messages;

namespace Fgvm.Cli.Command;

public sealed class SetCommand(IVersionManagementService versionManagementService, IAnsiConsole console, ILogger<SetCommand> logger)
{
    /// <summary>
    ///     Set the default Godot version.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="query">Version query arguments</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when installed versions cannot be read or no version can be
    ///     selected.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection is canceled.</exception>
    [Command("set")]
    public async Task Set(CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        try
        {
            _ = await versionManagementService.SetGlobalVersionAsync(query, cancellationToken: cancellationToken);
        }
        catch (TaskCanceledException)
        {
            logger.LogError($"User cancelled setting version.");
            console.MarkupLine(Messages.UserCancelled("setting version"));
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error setting a version: {Message}", e.Message);
            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to set the version")
            );

            throw;
        }
    }
}
