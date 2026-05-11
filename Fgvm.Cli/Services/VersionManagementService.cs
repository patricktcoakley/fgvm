using Fgvm.Cli.Error;
using Fgvm.Cli.Prompts;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ZLogger;

namespace Fgvm.Cli.Services;

/// <summary>
///     Service interface for managing Godot version operations
/// </summary>
public interface IVersionManagementService
{
    /// <summary>
    ///     Gets the host system for accessing system information
    /// </summary>
    IHostSystem HostSystem { get; }

    /// <summary>
    ///     Resolves the appropriate Godot version for launching in the current directory.
    ///     Handles project-specific version detection, compatibility checking, and automatic installation prompts.
    /// </summary>
    /// <param name="forceInteractive">Force interactive selection even if project version is detected</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Version resolution result containing execution path and working directory, or failure status</returns>
    /// <exception cref="OperationCanceledException">Thrown when version resolution is canceled.</exception>
    Task<Result<VersionResolutionOutcome, VersionResolutionError>> ResolveVersionForLaunchAsync(bool forceInteractive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves version for launching using only explicit .fgvm-version files (not project.godot auto-detection)
    /// </summary>
    /// <param name="forceInteractive">Force interactive selection even if .fgvm-version file is found</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Version resolution result containing execution path and working directory, or failure status</returns>
    /// <exception cref="OperationCanceledException">Thrown when version resolution is canceled.</exception>
    Task<Result<VersionResolutionOutcome, VersionResolutionError>> ResolveVersionForLaunchExplicitAsync(bool forceInteractive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets the global Godot version by creating/updating symlinks.
    /// </summary>
    /// <param name="query">Version query to search for</param>
    /// <param name="forceInteractive">Force interactive selection from installed versions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The release that was set</returns>
    /// <exception cref="InvalidOperationException">Thrown when installed versions cannot be read, no version can be selected, or symlink creation cannot continue.</exception>
    /// <exception cref="InvalidSymlinkException">Thrown when a symlink is created but validation fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection is canceled.</exception>
    Task<Release> SetGlobalVersionAsync(string[] query, bool forceInteractive = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves an interactive version selection to a version resolution result.
    /// </summary>
    /// <param name="selection">The version selection from interactive prompt</param>
    /// <returns>Version resolution result</returns>
    Result<VersionResolutionOutcome, VersionResolutionError> ResolveInteractiveVersion(string selection);

    /// <summary>
    ///     Sets the local project version by managing the `.fgvm-version` file.
    ///     Handles project detection, installation if needed, and file creation/updates.
    /// </summary>
    /// <param name="query">Version query to search for (null for auto-detection)</param>
    /// <param name="forceInteractive">Force interactive selection from installed versions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The release that was set</returns>
    /// <exception cref="ArgumentException">Thrown when a requested version query cannot be resolved during installation.</exception>
    /// <exception cref="InvalidOperationException">Thrown when project information, installed versions, installation state, or `.fgvm-version` writes cannot continue.</exception>
    /// <exception cref="OperationCanceledException">Thrown when interactive selection or installation is canceled.</exception>
    Task<Release> SetLocalVersionAsync(string[]? query = null, bool forceInteractive = false, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finds or installs a compatible version for the given project requirements.
    /// </summary>
    /// <param name="projectVersion">The version specified in the project</param>
    /// <param name="isDotNet">Whether the project uses .NET</param>
    /// <param name="promptForInstallation">Whether to prompt user for automatic installation if not found</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The compatible version if found/installed, null otherwise</returns>
    /// <exception cref="OperationCanceledException">Thrown when installation or interactive confirmation is canceled.</exception>
    Task<string?> FindOrInstallCompatibleVersionAsync(string projectVersion, bool isDotNet, bool promptForInstallation = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates or updates a `.fgvm-version` file in the specified directory.
    /// </summary>
    /// <param name="version">The version to write to the file</param>
    /// <param name="directory">The directory to create the file in (null for current directory)</param>
    /// <exception cref="InvalidOperationException">Thrown when the `.fgvm-version` file cannot be written.</exception>
    void CreateOrUpdateVersionFile(string version, string? directory = null);
}

/// <summary>
///     Service for managing Godot version operations including resolution, installation, and configuration
/// </summary>
public class VersionManagementService(
    IHostSystem hostSystem,
    IReleaseManager releaseManager,
    IInstallationService installationService,
    IInstallationOrchestrator installationOrchestrator,
    IPathService pathService,
    IProjectManager projectManager,
    IAnsiConsole console,
    ILogger<VersionManagementService> logger) : IVersionManagementService
{
    /// <inheritdoc />
    public IHostSystem HostSystem => hostSystem;

    /// <inheritdoc />
    public async Task<Result<VersionResolutionOutcome, VersionResolutionError>> ResolveVersionForLaunchAsync(bool forceInteractive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = ListInstallations();

            // Check for project-specific version first (unless interactive mode is requested)
            var projectInfo = FindProjectInfo();

            if (projectInfo is not null && !forceInteractive)
            {
                return await ResolveProjectVersionAsync(projectInfo, installed, cancellationToken);
            }

            if (forceInteractive)
            {
                return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
                    new VersionResolutionOutcome.InteractiveRequired(installed));
            }

            return ResolveSymlinkVersion();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError(e, $"Error resolving version for launch");
            console.MarkupLine(Messages.ErrorResolvingVersion);
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.Failed($"Error resolving version for launch: {e.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<Result<VersionResolutionOutcome, VersionResolutionError>> ResolveVersionForLaunchExplicitAsync(bool forceInteractive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = ListInstallations();

            // Check for explicit .fgvm-version file only (not project.godot auto-detection)
            var projectInfo = FindExplicitProjectInfo();

            if (projectInfo is not null && !forceInteractive)
            {
                return await ResolveProjectVersionAsync(projectInfo, installed, cancellationToken);
            }

            if (forceInteractive)
            {
                return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
                    new VersionResolutionOutcome.InteractiveRequired(installed));
            }

            return ResolveSymlinkVersion();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError(e, $"Error resolving version for launch");
            console.MarkupLine(Messages.ErrorResolvingVersion);
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.Failed($"Error resolving explicit version for launch: {e.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<Release> SetGlobalVersionAsync(string[] query, bool forceInteractive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = ListInstallations().ToArray();
            if (installed.Length == 0)
            {
                logger.ZLogWarning($"Tried to set a version when there were none installed.");
                throw new InvalidOperationException(Messages.NoInstallationsFound);
            }

            var versionToSet = forceInteractive || query.Length == 0
                // Drop to prompt when query is empty or interactive mode is forced
                ? await Set.ShowSetVersionPrompt(installed, console, cancellationToken)
                // Try to find the first release that matches the query or throw
                : releaseManager.FilterReleasesByQuery(query, installed).FirstOrDefault()
                  ?? throw new InvalidOperationException($"Unable to find Godot release with query `{string.Join(", ", query)}`");

            var godotRelease = CreateRelease(versionToSet);

            var symlinkTargetPath = Path.Combine(pathService.RootPath, godotRelease.ReleaseNameWithRuntime);
            symlinkTargetPath = Path.Combine(symlinkTargetPath, godotRelease.ExecName);
            var symlinkResult = hostSystem.CreateOrOverwriteSymbolicLink(symlinkTargetPath);

            if (symlinkResult is Result<Unit, SymlinkError>.Failure failure)
            {
                logger.ZLogError($"Failed to create symlink: {failure.Error}");

                switch (failure.Error)
                {
                    case SymlinkError.DeveloperModeRequired:
                        console.MarkupLine(Messages.DeveloperModeRequiredForSymlink);
                        return godotRelease;
                    case SymlinkError.PermissionDenied:
                        console.MarkupLine(Messages.SymlinkPermissionDenied);
                        return godotRelease;
                    case SymlinkError.UnsupportedOS(var os):
                        console.MarkupLine(Messages.SymlinkUnsupportedOS(os));
                        return godotRelease;
                    case SymlinkError.InvalidSymlink(var path, var target):
                        throw new InvalidSymlinkException(target, path);
                }
            }

            logger.ZLogInformation($"Successfully set version to {godotRelease.ReleaseNameWithRuntime}.");
            console.MarkupLine(Messages.SuccessfullySetVersion(godotRelease.ReleaseNameWithRuntime));

            return godotRelease;
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error setting a version: {e.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Release> SetLocalVersionAsync(string[]? query = null, bool forceInteractive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = ListInstallations().ToArray();
            var versionToSet = await DetermineVersionToSetAsync(query, forceInteractive, installed, cancellationToken);

            var godotRelease = CreateRelease(versionToSet);

            // Create the `.fgvm-version` file
            CreateOrUpdateVersionFile(godotRelease.ReleaseNameWithRuntime);

            var versionFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".fgvm-version");
            var fileExists = File.Exists(versionFilePath);

            logger.ZLogInformation($"Successfully set local version to {godotRelease.ReleaseNameWithRuntime}.");
            console.MarkupLine(fileExists ? Messages.UpdatedVersionFile : Messages.CreatedVersionFile);

            return godotRelease;
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error setting local version: {e.Message}");
            throw;
        }
    }

    // TODO: Replace with Task<Result<string, VersionError>> FindOrInstallCompatibleVersionAsync(...)
    /// <inheritdoc />
    public async Task<string?> FindOrInstallCompatibleVersionAsync(string projectVersion, bool isDotNet, bool promptForInstallation = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var installed = ListInstallations();

            // First try to find a compatible installed version
            var compatibleVersion = TryFindCompatibleVersion(projectVersion, isDotNet, installed);
            if (compatibleVersion is not null)
            {
                return compatibleVersion;
            }

            // No compatible version found, prompt only when requested
            if (promptForInstallation && !await PromptForInstallationAsync(projectVersion, isDotNet, cancellationToken))
            {
                return null;
            }

            console.MarkupLine(Messages.InstallingAutoDetected(projectVersion, isDotNet ? " (.NET)" : ""));

            // Build the query for installation - strip any existing runtime suffix and use isDotNet to determine correct runtime
            var baseVersion = projectVersion switch
            {
                _ when projectVersion.EndsWith("-mono") => projectVersion[..^5],
                _ when projectVersion.EndsWith("-standard") => projectVersion[..^9],
                _ => projectVersion
            };

            var installQuery = isDotNet
                ? new[] { baseVersion, "mono" }
                : new[] { baseVersion };

            var installationResult = await installationOrchestrator.InstallAsync(installQuery, cancellationToken: cancellationToken);

            if (installationResult is not Result<InstallationOutcome, InstallationError>.Success installSuccess)
            {
                return null;
            }

            // Re-check for a compatible version after installation
            installed = ListInstallations();
            compatibleVersion = TryFindCompatibleVersion(projectVersion, isDotNet, installed);

            // If installation succeeded but compatibility check failed, return the installed version name
            if (compatibleVersion == null)
            {
                var releaseNameWithRuntime = installSuccess.Value switch
                {
                    InstallationOutcome.NewInstallation(var name, _, _) => name,
                    InstallationOutcome.AlreadyInstalled(var name) => name,
                    _ => throw new InvalidOperationException(Messages.UnknownInstallationOutcome)
                };

                return releaseNameWithRuntime;
            }

            return compatibleVersion;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError(e, $"Error finding or installing compatible version {projectVersion}");
            return null;
        }
    }

    /// <inheritdoc />
    public void CreateOrUpdateVersionFile(string version, string? directory = null)
    {
        if (projectManager.CreateVersionFile(version, directory) is Result<Unit, ProjectError>.Failure)
        {
            throw new InvalidOperationException("Unable to write `.fgvm-version` file.");
        }
    }

    /// <inheritdoc />
    public Result<VersionResolutionOutcome, VersionResolutionError> ResolveInteractiveVersion(string selection)
    {
        if (releaseManager.CreateRelease(selection) is not Result<Release, ReleaseParseError>.Success godotReleaseResult)
        {
            logger.ZLogError($"Invalid Godot version selected: {selection}");
            console.MarkupLine($"[red]Invalid Godot version: {selection}[/]");
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.InvalidVersion(selection));
        }

        var godotRelease = godotReleaseResult.Value;
        var (execPath, workingDirectory) = GetExecutionPaths(godotRelease);
        return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
            new VersionResolutionOutcome.Found(execPath, workingDirectory, selection, false));
    }

    private async Task<Result<VersionResolutionOutcome, VersionResolutionError>> ResolveProjectVersionAsync(Release projectRelease,
        List<string> installed,
        CancellationToken cancellationToken)
    {
        var projectVersion = projectRelease.ReleaseNameWithRuntime;

        // Try to find a compatible installed version
        var compatibleVersion = TryFindCompatibleVersion(projectVersion, projectRelease.IsDotNet, installed);

        if (compatibleVersion is not null)
        {
            return CreateVersionResolutionResult(compatibleVersion, projectRelease, projectVersion, true);
        }

        logger.ZLogWarning($"Project version {projectVersion} is not installed.");

        // Prompt user for automatic installation
        if (!await PromptForInstallationAsync(projectVersion, projectRelease.IsDotNet, cancellationToken))
        {
            console.MarkupLine(Messages.ProjectVersionNotInstalled(projectVersion, projectRelease.RuntimeDisplaySuffix));
            console.MarkupLine(Messages.InstallationInstructions(projectVersion, projectRelease.IsDotNet));

            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.NotFound(projectVersion));
        }

        var compatibleInstalled = await FindOrInstallCompatibleVersionAsync(projectVersion, projectRelease.IsDotNet, false, cancellationToken);
        if (compatibleInstalled is null)
        {
            console.MarkupLine(Messages.FailedToInstallProjectVersion(projectVersion, projectRelease.RuntimeDisplaySuffix));
            console.MarkupLine(Messages.ManualInstallInstructions(projectVersion, projectRelease.IsDotNet));
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.Failed($"Failed to install {projectVersion}{projectRelease.RuntimeDisplaySuffix}"));
        }

        // Re-get installed versions and resolve again
        var updatedInstalled = ListInstallations();
        var newCompatibleVersion = TryFindCompatibleVersion(projectVersion, projectRelease.IsDotNet, updatedInstalled);

        if (newCompatibleVersion is null ||
            releaseManager.CreateRelease(newCompatibleVersion) is not Result<Release, ReleaseParseError>.Success newProjectGodotReleaseResult)
        {
            console.MarkupLine(Messages.InstallationSucceededButNotFound);
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.Failed($"Installation succeeded but version {projectVersion} not found in installed list"));
        }

        var newProjectGodotRelease = newProjectGodotReleaseResult.Value;
        var (execPath, workingDirectory) = GetExecutionPaths(newProjectGodotRelease);

        console.MarkupLine(Messages.SuccessfullyInstalledAndUsing(projectVersion, projectRelease.RuntimeDisplaySuffix, newCompatibleVersion));

        return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
            new VersionResolutionOutcome.Found(execPath, workingDirectory, newCompatibleVersion, true));
    }

    private Result<VersionResolutionOutcome, VersionResolutionError> ResolveSymlinkVersion()
    {
        if (!Path.Exists(pathService.SymlinkPath))
        {
            logger.ZLogError($"Tried to launch when no version is set.");
            if (hostSystem.AreSymlinksSupported() is Result<Unit, SymlinkError>.Failure supportFailure)
            {
                switch (supportFailure.Error)
                {
                    case SymlinkError.DeveloperModeRequired:
                        console.MarkupLine(Messages.DeveloperModeRequiredForGodot);
                        break;
                    case SymlinkError.PermissionDenied:
                        console.MarkupLine(Messages.SymlinkPermissionDenied);
                        break;
                    case SymlinkError.UnsupportedOS(var os):
                        console.MarkupLine(Messages.SymlinkUnsupportedOS(os));
                        break;
                    default:
                        console.MarkupLine(Messages.NoCurrentVersionSet);
                        break;
                }
            }
            else
            {
                console.MarkupLine(Messages.NoCurrentVersionSet);
            }

            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.NotFound("No current version set"));
        }

        var symlinkInfo = new FileInfo(pathService.SymlinkPath);
        var execPath = symlinkInfo.ResolveLinkTarget(true)!.FullName;
        var workingDirectory = "";
        var versionName = "Unknown";

        if (symlinkInfo.LinkTarget is not { } target)
        {
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
                new VersionResolutionOutcome.Found(execPath, workingDirectory, versionName, false));
        }

        var split = target.Split(Path.DirectorySeparatorChar)[..^1];
        workingDirectory = string.Join(Path.DirectorySeparatorChar, split);

        // Extract version name from the symlink target path
        var targetParts = target.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Look for a part that looks like a version (contains numbers and hyphens)
        foreach (var part in targetParts)
        {
            if (string.IsNullOrEmpty(part) ||
                !part.Contains('-') ||
                !part.Any(char.IsDigit) ||
                part.Equals("MacOS", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Contents", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            versionName = part;
            break;
        }

        return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
            new VersionResolutionOutcome.Found(execPath, workingDirectory, versionName, false));
    }

    private Result<VersionResolutionOutcome, VersionResolutionError> CreateVersionResolutionResult(string compatibleVersion, Release projectRelease,
        string projectVersion,
        bool isProjectVersion = false)
    {
        if (releaseManager.CreateRelease(compatibleVersion) is not Result<Release, ReleaseParseError>.Success projectGodotReleaseResult)
        {
            logger.ZLogError($"Invalid project version: {compatibleVersion}");
            console.MarkupLine(Messages.InvalidProjectVersion(compatibleVersion));
            return new Result<VersionResolutionOutcome, VersionResolutionError>.Failure(
                new VersionResolutionError.InvalidVersion(compatibleVersion));
        }

        var projectGodotRelease = projectGodotReleaseResult.Value;
        var (execPath, workingDirectory) = GetExecutionPaths(projectGodotRelease);

        console.MarkupLine(Messages.UsingProjectVersion(projectVersion, projectRelease.RuntimeDisplaySuffix, compatibleVersion));
        return new Result<VersionResolutionOutcome, VersionResolutionError>.Success(
            new VersionResolutionOutcome.Found(execPath, workingDirectory, compatibleVersion, isProjectVersion));
    }

    private (string execPath, string workingDirectory) GetExecutionPaths(Release release)
    {
        var workingDirectory = Path.Combine(pathService.RootPath, release.ReleaseNameWithRuntime);
        var execPath = Path.Combine(workingDirectory, release.ExecName);

        // Handle macOS app bundles by pointing to the actual executable inside
        if (execPath.EndsWith(".app"))
        {
            execPath = Path.Combine(execPath, "Contents", "MacOS", "Godot");
        }

        return (execPath, workingDirectory);
    }

    private List<string> ListInstallations() =>
        hostSystem.ListInstallations() switch
        {
            Result<IReadOnlyList<string>, FileSystemError>.Success(var installations) => installations.ToList(),
            Result<IReadOnlyList<string>, FileSystemError>.Failure =>
                throw new InvalidOperationException("Unable to read installed Godot versions."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private Release? FindProjectInfo() =>
        projectManager.FindProjectInfo() switch
        {
            Result<ProjectLookup<Release>, ProjectError>.Success(ProjectLookup<Release>.Found(var release)) => release,
            Result<ProjectLookup<Release>, ProjectError>.Success => null,
            Result<ProjectLookup<Release>, ProjectError>.Failure =>
                throw new InvalidOperationException("Unable to read project version information."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private Release? FindExplicitProjectInfo() =>
        projectManager.FindExplicitProjectInfo() switch
        {
            Result<ProjectLookup<Release>, ProjectError>.Success(ProjectLookup<Release>.Found(var release)) => release,
            Result<ProjectLookup<Release>, ProjectError>.Success => null,
            Result<ProjectLookup<Release>, ProjectError>.Failure =>
                throw new InvalidOperationException("Unable to read `.fgvm-version` information."),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private Release CreateRelease(string version) =>
        releaseManager.CreateRelease(version) switch
        {
            Result<Release, ReleaseParseError>.Success(var release) => release,
            Result<Release, ReleaseParseError>.Failure => throw new InvalidOperationException(Messages.InvalidGodotVersion),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private string? TryFindCompatibleVersion(string projectVersion, bool isDotNet, IEnumerable<string> installedVersions) =>
        releaseManager.FindCompatibleVersionResult(projectVersion, isDotNet, installedVersions) switch
        {
            Result<string, CompatibilityError>.Success(var version) => version,
            Result<string, CompatibilityError>.Failure => null,
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private async Task<string> DetermineVersionToSetAsync(string[]? query, bool forceInteractive, string[] installed, CancellationToken cancellationToken)
    {
        if (query == null || query.Length == 0)
        {
            return await HandleAutoDetectionModeAsync(installed, forceInteractive, cancellationToken);
        }

        return await HandleQueryModeAsync(query, installed, cancellationToken);
    }


    private async Task<string> HandleAutoDetectionModeAsync(string[] installed, bool forceInteractive, CancellationToken cancellationToken)
    {
        // Check for existing `.fgvm-version` file or `project.godot`
        var projectInfo = FindProjectInfo();
        if (projectInfo is not null)
        {
            return await HandleProjectInfoAsync(projectInfo, installed, forceInteractive, cancellationToken);
        }

        if (installed.Length == 0)
        {
            logger.ZLogWarning($"No versions installed and no `.fgvm-version` file found.");
            throw new InvalidOperationException(Messages.NoInstallationsAndNoVersionFile);
        }

        // No `.fgvm-version` found, prompt for selection
        return await Set.ShowSetVersionPrompt(installed, console, cancellationToken);
    }

    private async Task<string> HandleProjectInfoAsync(Release projectRelease, string[] installed, bool forceInteractive,
        CancellationToken cancellationToken)
    {
        var projectVersion = projectRelease.ReleaseNameWithRuntime;

        // If interactive mode is forced, show selection regardless of compatible versions
        if (forceInteractive)
        {
            if (installed.Length <= 0)
            {
                throw new InvalidOperationException(Messages.NoVersionsInstalledPrompt);
            }

            console.MarkupLine(Messages.ProjectSpecifiesVersion(projectVersion, projectRelease.RuntimeDisplaySuffix));
            console.MarkupLine(Messages.ChooseFromInstalled);
            return await Set.ShowSetVersionPrompt(installed, console, cancellationToken);
        }

        // Try to find a compatible installed version
        var compatibleVersion = TryFindCompatibleVersion(projectVersion, projectRelease.IsDotNet, installed);

        if (compatibleVersion is not null)
        {
            // Compatible version already installed, create the file quietly
            return compatibleVersion;
        }

        // Not installed, auto-install
        logger.ZLogInformation($"Project version {projectVersion} is not installed, automatically installing it.");
        console.MarkupLine(Messages.ProjectVersionNotInstalled(projectVersion, projectRelease.RuntimeDisplaySuffix));

        // We already have projectRelease validated, no need to revalidate
        console.MarkupLine(Messages.InstallingProjectVersion(projectVersion, projectRelease.RuntimeDisplaySuffix));

        // Use the exact version with runtime as the install query
        string[] installQuery = [projectRelease.ReleaseNameWithRuntime];
        var installedRelease =
            await installationService.InstallByQueryAsync(installQuery, new Progress<OperationProgress<InstallationStage>>(), cancellationToken: cancellationToken);

        if (installedRelease is not Result<InstallationOutcome, InstallationError>.Success installSuccess)
        {
            console.MarkupLine(Messages.FailedToInstallProjectVersion(projectVersion, projectRelease.RuntimeDisplaySuffix));

            if (installed.Length <= 0)
            {
                throw new InvalidOperationException("No versions installed. Install a version first with: fgvm install <version>");
            }

            console.MarkupLine(Messages.ChooseFromInstalled);
            return await Set.ShowSetVersionPrompt(installed, console, cancellationToken);
        }

        var releaseNameWithRuntime = installSuccess.Value switch
        {
            InstallationOutcome.NewInstallation(var name, _, _) => name,
            InstallationOutcome.AlreadyInstalled(var name) => name,
            _ => throw new InvalidOperationException(Messages.UnknownInstallationOutcome)
        };

        console.MarkupLine(Messages.SuccessfullyInstalled(releaseNameWithRuntime));
        return releaseNameWithRuntime;
    }

    private async Task<string> HandleQueryModeAsync(string[] query, string[] installed, CancellationToken cancellationToken)
    {
        // Use query to find version - try to find exact match first
        switch (releaseManager.ResolveReleaseQuery(query, installed))
        {
            case Result<Release, QueryError>.Success(var release):
                return release.ReleaseNameWithRuntime;
            case Result<Release, QueryError>.Failure(QueryError.NotFound):
                break;
            case Result<Release, QueryError>.Failure(var error):
                throw new ArgumentException(GetQueryErrorMessage(error, query));
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        logger.ZLogInformation($"Version matching '{string.Join(" ", query)}' not installed, attempting to install it.");
        console.MarkupLine(Messages.NoInstalledVersionMatching(string.Join(" ", query)));
        console.MarkupLine(Messages.Installing(string.Join(" ", query)));

        // Try to install
        var installedRelease =
            await installationService.InstallByQueryAsync(query, new Progress<OperationProgress<InstallationStage>>(), cancellationToken: cancellationToken);

        if (installedRelease is not Result<InstallationOutcome, InstallationError>.Success installSuccess)
        {
            console.MarkupLine(Messages.FailedToInstallMatching(string.Join(" ", query)));

            if (installedRelease is Result<InstallationOutcome, InstallationError>.Failure(InstallationError.NotFound notFound))
            {
                throw new ArgumentException(Messages.InstallationNotFound(notFound.Version, hostSystem));
            }

            if (installedRelease is Result<InstallationOutcome, InstallationError>.Failure(InstallationError.InvalidQuery invalidQuery))
            {
                throw new ArgumentException(invalidQuery.Message);
            }

            if (installed.Length <= 0)
            {
                throw new InvalidOperationException(Messages.InstallationFailedNoVersions);
            }

            console.MarkupLine(Messages.ChooseFromInstalled);
            return await Set.ShowSetVersionPrompt(installed, console, cancellationToken);
        }

        // Re-check installed versions after installation
        var updatedInstalled = ListInstallations().ToArray();
        string? foundVersion;
        switch (releaseManager.ResolveReleaseQuery(query, updatedInstalled))
        {
            case Result<Release, QueryError>.Success(var release):
                foundVersion = release.ReleaseNameWithRuntime;
                break;
            case Result<Release, QueryError>.Failure(QueryError.NotFound):
                foundVersion = null;
                break;
            case Result<Release, QueryError>.Failure(var error):
                throw new ArgumentException(GetQueryErrorMessage(error, query));
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        if (foundVersion == null)
        {
            console.MarkupLine(Messages.InstallationSucceededButNotFound);
            throw new InvalidOperationException(Messages.InstallationSucceededButNotFound);
        }

        var releaseNameWithRuntime = installSuccess.Value switch
        {
            InstallationOutcome.NewInstallation(var name, _, _) => name,
            InstallationOutcome.AlreadyInstalled(var name) => name,
            _ => throw new InvalidOperationException(Messages.UnknownInstallationOutcome)
        };

        console.MarkupLine(Messages.SuccessfullyInstalled(releaseNameWithRuntime));
        return foundVersion;
    }

    private static string GetQueryErrorMessage(QueryError error, string[] query) => error switch
    {
        QueryError.EmptyQuery => "Version query is required.",
        QueryError.InvalidQuery invalid => invalid.Message,
        QueryError.NotFound notFound => $"Unable to find Godot release with query `{notFound.Query}`.",
        _ => $"Unable to find Godot release with query `{string.Join(" ", query)}`."
    };

    /// <summary>
    ///     Creates a confirmation prompt for automatic installation.
    /// </summary>
    private async Task<bool> PromptForInstallationAsync(string projectVersion, bool isDotNet, CancellationToken cancellationToken)
    {
        try
        {
            var runtimeText = isDotNet ? " [[.NET]]" : "";

            var confirmPrompt =
                new ConfirmationPrompt(Messages.ProjectRequiresInstall(projectVersion, runtimeText))
                {
                    DefaultValue = true
                };

            return await confirmPrompt.ShowAsync(console, cancellationToken);
        }
        catch (Exception)
        {
            // If prompting fails (e.g., non-interactive environment), default to false
            return false;
        }
    }
}
