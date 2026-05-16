using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class RemoveCommand(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<RemoveCommand> logger)
{
    /// <summary>
    ///     Remove an installed Godot version.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <param name="query"></param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when installed versions, registry state, or symlinks cannot be read
    ///     or removed.
    /// </exception>
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
                ClearDefault();
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

            var removedSymlinks = false;
            foreach (var version in versionsToDelete)
            {
                var installation = FindInstallation(version);
                var removingDefault = IsDefaultInstallation(installation.Key);
                var selectionPath = Path.Combine(pathService.RootPath, installation.RelativePath);
                switch (hostSystem.DirectoryExists(selectionPath))
                {
                    case Result<bool, FileOperationError>.Failure(var existsError):
                        throw new InvalidOperationException($"Unable to read installation path `{selectionPath}`: {existsError}");
                    case Result<bool, FileOperationError>.Success { Value: true }:
                        if (hostSystem.DeleteDirectoryIfExists(selectionPath, true) is Result<Unit, FileOperationError>.Failure(var deleteError))
                        {
                            throw new InvalidOperationException($"Unable to remove installation `{selectionPath}`: {deleteError}");
                        }

                        logger.ZLogInformation($"Removed installation: {version}");
                        console.MarkupLine(Messages.SuccessfullyRemoved(selectionPath));
                        break;
                    case Result<bool, FileOperationError>.Success { Value: false }:
                    case null:
                        logger.ZLogWarning($"Installation {version} does not exist at {selectionPath}, skipping removal.");
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }

                if (installationRegistry.Remove(installation.Key) is Result<Unit, InstallationRegistryError>.Failure(var removeError))
                {
                    throw new InvalidOperationException($"Unable to update installation registry: {removeError}");
                }

                if (removingDefault)
                {
                    RemoveSymbolicLinks();
                    removedSymlinks = true;
                }
            }

            if (!removedSymlinks && ListInstallations().Length == 0)
            {
                logger.LogInformation("No installations remaining, removing Godot symlinks.");
                RemoveSymbolicLinks();
            }
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled removal.");
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
        installationRegistry.ListInstallations() switch
        {
            Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(var installations) =>
                installations.Select(installation => installation.ReleaseNameWithRuntime).ToArray(),
            Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure =>
                throw new InvalidOperationException("Unable to read installed Godot versions."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private Installation FindInstallation(string releaseNameWithRuntime) =>
        installationRegistry.FindByReleaseName(releaseNameWithRuntime) switch
        {
            Result<Installation, InstallationRegistryError>.Success(var installation) => installation,
            Result<Installation, InstallationRegistryError>.Failure =>
                throw new InvalidOperationException($"Unable to find installed Godot version `{releaseNameWithRuntime}`."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private bool IsDefaultInstallation(string key) =>
        installationRegistry.GetDefault() switch
        {
            Result<Installation, InstallationRegistryError>.Success(var installation) =>
                string.Equals(installation.Key, key, StringComparison.Ordinal),
            Result<Installation, InstallationRegistryError>.Failure(InstallationRegistryError.NotFound) => false,
            Result<Installation, InstallationRegistryError>.Failure =>
                throw new InvalidOperationException("Unable to read default Godot installation."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private void ClearDefault()
    {
        if (installationRegistry.ClearDefault() is Result<Unit, InstallationRegistryError>.Failure)
        {
            throw new InvalidOperationException("Unable to clear default Godot installation.");
        }
    }

    private void RemoveSymbolicLinks()
    {
        if (hostSystem.RemoveSymbolicLinks() is Result<Unit, SymlinkError>.Failure)
        {
            throw new InvalidOperationException("Unable to remove Godot symlinks.");
        }
    }
}
