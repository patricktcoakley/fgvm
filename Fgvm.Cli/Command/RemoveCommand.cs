using ConsoleAppFramework;
using Fgvm.Cli.Error;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;


namespace Fgvm.Cli.Command;

public sealed class RemoveCommand(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IInstallationRegistry installationRegistry,
    ITemplateRegistry templateRegistry,
    ITemplateOrchestrator templateOrchestrator,
    IRemovalService removalService,
    IPathService pathService,
    IAnsiConsole console,
    ILogger<RemoveCommand> logger
)
{
    /// <summary>
    ///     Remove an installed Godot version.
    /// </summary>
    /// <param name="withTemplates">Remove export templates matching each removed editor.</param>
    /// <param name="cancellationToken"></param>
    /// <param name="query"></param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when installed versions, registry state, or symlinks cannot be read
    ///     or removed.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when removal is canceled.</exception>
    [Command("remove|r")]
    public async Task Remove(bool withTemplates = false,
        CancellationToken cancellationToken = default,
        [Argument] params string[] query
    )
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
                if (withTemplates)
                {
                    await RemoveTemplatesMatching(query, cancellationToken);
                }

                return;
            }

            var filteredInstallations = releaseManager.FilterReleasesByQuery(query, installed).ToArray();
            if (filteredInstallations.Length == 0)
            {
                var queryJoin = string.Join(' ', query);
                logger.LogInformation("Query didn't find any installations: {QueryJoin}.", queryJoin);
                console.MarkupLine(Messages.NoVersionsMatchingQuery(queryJoin));
                if (withTemplates)
                {
                    await RemoveTemplatesMatching(query, cancellationToken);
                }

                return;
            }

            IEnumerable<string> versionsToDelete;
            if (filteredInstallations.Length == 1)
            {
                var versionToRemove = filteredInstallations[0];
                logger.LogInformation("Automatically removing single matched version: {VersionToRemove}.", versionToRemove);
                console.MarkupLine(Messages.FoundExactMatch(versionToRemove));
                versionsToDelete = [versionToRemove];
            }
            else
            {
                if (!console.Profile.Capabilities.Interactive)
                {
                    throw new ArgumentException(Messages.AmbiguousQueryInNonInteractiveShell(
                        "fgvm remove", string.Join(' ', query), filteredInstallations));
                }

                versionsToDelete = await Prompts.Remove.ShowVersionRemovalPrompt(filteredInstallations, console, cancellationToken);
            }

            var selectedInstallations = versionsToDelete.Select(FindInstallation).ToArray();
            var templatesToRemove = withTemplates
                ? FindTemplates(selectedInstallations)
                : [];

            var editorPaths = new List<string>();
            foreach (var installation in selectedInstallations)
            {
                var selectionPath = Path.Combine(pathService.RootPath, installation.RelativePath);
                switch (hostSystem.DirectoryExists(selectionPath))
                {
                    case Result<bool, FileOperationError>.Failure(var existsError):
                        throw new InvalidOperationException($"Unable to read installation path `{selectionPath}`: {existsError}");
                    case Result<bool, FileOperationError>.Success { Value: true }:
                        editorPaths.Add(selectionPath);
                        break;
                    case Result<bool, FileOperationError>.Success { Value: false }:
                    case null:
                        logger.LogWarning("Installation {Version} does not exist at {SelectionPath}, skipping removal.",
                            installation.ReleaseNameWithRuntime,
                            selectionPath);
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }
            }

            // Read once: GetDefault re-reads and re-validates the whole registry, and can rewrite it
            var defaultKey = GetDefaultKey();
            var removingDefault = selectedInstallations.Any(installation =>
                string.Equals(installation.Key, defaultKey, StringComparison.Ordinal));
            var pathsToRemove = editorPaths.Concat(templatesToRemove.Select(template => template.Path));

            // Not cancellable past this point; the rename and delete are over before anyone could interrupt
            var staged = removalService.Stage(pathsToRemove);

            foreach (var installation in selectedInstallations)
            {
                if (installationRegistry.Remove(installation.Key) is
                    Result<Unit, InstallationRegistryError>.Failure(var removeError))
                {
                    throw new InvalidOperationException($"Unable to update installation registry: {removeError}");
                }

                logger.LogInformation("Removed installation: {Version}", installation.ReleaseNameWithRuntime);
            }

            foreach (var editorPath in editorPaths)
            {
                console.MarkupLine(Messages.SuccessfullyRemoved(editorPath));
            }

            removalService.WriteTemplateRemovalMessages(templatesToRemove);
            if (removingDefault || ListInstallations().Length == 0)
            {
                logger.LogInformation("Removed the default or final installation, removing Godot symlinks.");
                RemoveSymbolicLinks();
            }

            // Deleted last so the user isn't waiting on a large recursive delete before seeing the result
            removalService.Discard(staged);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("User cancelled removal.");
            console.MarkupLine(Messages.UserCancelled("removal"));

            throw;
        }
        // Argument errors carry their own actionable message and exit code; the generic notice below would bury it.
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error removing installations: {Message}", e.Message);
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

    private string? GetDefaultKey() =>
        installationRegistry.GetDefault() switch
        {
            Result<Installation, InstallationRegistryError>.Success(var installation) => installation.Key,
            Result<Installation, InstallationRegistryError>.Failure(InstallationRegistryError.NotFound) => null,
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

    // Not SelectForRemovalAsync: that one prints and prompts, which has no place once the user has already chosen
    private IReadOnlyList<TemplateInstallation> FindTemplates(IEnumerable<Installation> installations)
    {
        var templates = new List<TemplateInstallation>();
        foreach (var installation in installations)
        {
            switch (templateRegistry.FindByReleaseName(installation.ReleaseNameWithRuntime))
            {
                case Result<TemplateInstallation, TemplateRegistryError>.Success(var template):
                    templates.Add(template);
                    break;
                case Result<TemplateInstallation, TemplateRegistryError>.Failure(TemplateRegistryError.NotFound):
                    break;
                case Result<TemplateInstallation, TemplateRegistryError>.Failure(var error):
                    throw new InvalidOperationException(
                        $"Unable to read export templates for `{installation.ReleaseNameWithRuntime}`: {error}");
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }

        return templates;
    }

    // Selection is interactive here because the user hasn't picked anything yet
    private async Task RemoveTemplatesMatching(string[] query, CancellationToken cancellationToken)
    {
        var templates = (await templateOrchestrator.SelectForRemovalAsync(query, cancellationToken)) switch
        {
            Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success(var installations) => installations,
            Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Failure(var error) =>
                throw new InvalidOperationException($"Unable to select export templates for removal: {error}"),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

        RemoveTemplates(templates);
    }

    private void RemoveTemplates(IReadOnlyList<TemplateInstallation> templates)
    {
        if (templates.Count == 0)
        {
            return;
        }

        var staged = removalService.Stage(templates.Select(template => template.Path));
        removalService.WriteTemplateRemovalMessages(templates);
        removalService.Discard(staged);
    }
}
