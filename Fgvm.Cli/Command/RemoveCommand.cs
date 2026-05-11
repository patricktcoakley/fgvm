using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class RemoveCommand(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<RemoveCommand> logger)
{
    /// <summary>
    ///     Remove an installed Godot version.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <param name="query"></param>
    /// <exception cref="InvalidOperationException">Thrown when installed versions or symlinks cannot be read or removed.</exception>
    /// <exception cref="OperationCanceledException">Thrown when removal is canceled.</exception>
    [Command("remove|r")]
    public async Task Remove(CancellationToken cancellationToken = default, [Argument] params string[] query)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var installed = ListInstallations();

            if (installed.Length == 0)
            {
                RemoveSymbolicLinks();
                console.MarkupLine(Messages.NoInstallationsToRemove);
                return;
            }

            var filteredInstallations = releaseManager.FilterReleasesByQuery(query, installed).ToArray();
            if (filteredInstallations.Length == 0)
            {
                var queryJoin = string.Join(' ', query);
                logger.ZLogInformation($"Query didn't find any installations: {queryJoin}.");
                console.MarkupLine(Messages.NoVersionsMatchingQuery(queryJoin));
                return;
            }

            IEnumerable<string> versionsToDelete;
            if (filteredInstallations.Length == 1)
            {
                var versionToRemove = filteredInstallations[0];
                logger.ZLogInformation($"Automatically removing single matched version: {versionToRemove}.");
                console.MarkupLine(Messages.FoundExactMatch(versionToRemove));
                versionsToDelete = [versionToRemove];
            }
            else
            {
                versionsToDelete = await Prompts.Remove.ShowVersionRemovalPrompt(filteredInstallations, console, cancellationToken);
            }

            foreach (var version in versionsToDelete)
            {
                var selectionPath = Path.Combine(pathService.RootPath, version);
                if (Directory.Exists(selectionPath))
                {
                    Directory.Delete(selectionPath, true);
                    logger.ZLogInformation($"Removed installation: {version}");
                    console.MarkupLine(Messages.SuccessfullyRemoved(selectionPath));
                }
                else
                {
                    logger.ZLogWarning($"Installation {version} does not exist at {selectionPath}, skipping removal.");
                }
            }

            if (ListInstallations().Length == 0)
            {
                logger.ZLogInformation($"No installations remaining, removing symbolic links.");
                RemoveSymbolicLinks();
            }
        }
        catch (TaskCanceledException)
        {
            logger.ZLogError($"User cancelled removal.");
            console.MarkupLine(Messages.UserCancelled("removal"));

            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error removing installations: {e.Message}");
            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to remove installations", pathService)
            );

            throw;
        }
    }

    private string[] ListInstallations() =>
        hostSystem.ListInstallations() switch
        {
            Result<IReadOnlyList<string>, FileSystemError>.Success(var installations) => installations.ToArray(),
            Result<IReadOnlyList<string>, FileSystemError>.Failure =>
                throw new InvalidOperationException("Unable to read installed Godot versions."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private void RemoveSymbolicLinks()
    {
        if (hostSystem.RemoveSymbolicLinks() is Result<Unit, SymlinkError>.Failure)
        {
            throw new InvalidOperationException("Unable to remove Godot symlinks.");
        }
    }
}
