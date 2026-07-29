using Fgvm.Cli.Error;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Fgvm.Cli.Services;

/// <summary>
///     Shared removal plumbing for the commands that delete installed directories.
/// </summary>
public interface IRemovalService
{
    /// <summary>
    ///     Renames directories aside. Update the installation registry after this, never before: a registry entry with
    ///     no directory repairs itself on the next read, but a directory with no entry stays invisible.
    /// </summary>
    /// <param name="directoryPaths">The directories to remove.</param>
    /// <returns>The staged directories, to delete with <see cref="Discard" /> once the registry is updated.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a directory cannot be renamed aside.</exception>
    IReadOnlyList<string> Stage(IEnumerable<string> directoryPaths);

    /// <summary>
    ///     Deletes staged directories. Never throws; anything left behind is deleted by the next <see cref="Sweep" />.
    /// </summary>
    /// <param name="stagedPaths">Paths returned by <see cref="Stage" />.</param>
    void Discard(IEnumerable<string> stagedPaths);

    /// <summary>
    ///     Deletes anything an interrupted removal left behind. Never throws.
    /// </summary>
    void Sweep();

    /// <summary>
    ///     Reports removed export templates, one line per template version.
    /// </summary>
    /// <param name="templates">The removed templates.</param>
    void WriteTemplateRemovalMessages(IEnumerable<TemplateInstallation> templates);
}

public sealed class RemovalService(
    IDirectoryRemoval directoryRemoval,
    IHostSystem hostSystem,
    IPathService pathService,
    IGodotPathService godotPathService,
    IAnsiConsole console,
    ILogger<RemovalService> logger
) : IRemovalService
{
    /// <inheritdoc />
    public IReadOnlyList<string> Stage(IEnumerable<string> directoryPaths) =>
        directoryRemoval.Stage(directoryPaths) switch
        {
            Result<IReadOnlyList<string>, FileOperationError>.Success(var staged) => staged,
            Result<IReadOnlyList<string>, FileOperationError>.Failure(var error) =>
                throw new InvalidOperationException($"Unable to remove installation directory: {error}"),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    /// <inheritdoc />
    public void Discard(IEnumerable<string> stagedPaths) =>
        directoryRemoval.Discard(stagedPaths);

    /// <inheritdoc />
    public void Sweep()
    {
        var stagingDirectories = new List<string>();
        AddStagingDirectory(stagingDirectories, () => pathService.RootPath, "fgvm root");
        AddStagingDirectory(stagingDirectories, () => godotPathService.ExportTemplatesRootPath,
            "Godot export templates root");

        var installationsDirectory = ResolveStagingDirectory(
            () => pathService.InstallationsDirectoryPath,
            "fgvm installations root");
        if (installationsDirectory is not null)
        {
            stagingDirectories.Add(installationsDirectory);
            AddReleaseDirectories(stagingDirectories, installationsDirectory);
        }

        directoryRemoval.Sweep(stagingDirectories);
    }

    /// <inheritdoc />
    public void WriteTemplateRemovalMessages(IEnumerable<TemplateInstallation> templates)
    {
        foreach (var template in templates.DistinctBy(template => template.TemplateVersion, StringComparer.Ordinal))
        {
            console.MarkupLine(Messages.TemplateSuccessfullyRemoved(template.TemplateVersion, template.Path));
        }
    }

    private void AddStagingDirectory(ICollection<string> stagingDirectories,
        Func<string> resolvePath,
        string description
    )
    {
        if (ResolveStagingDirectory(resolvePath, description) is { } path)
        {
            stagingDirectories.Add(path);
        }
    }

    private string? ResolveStagingDirectory(Func<string> resolvePath, string description)
    {
        try
        {
            return resolvePath();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or ArgumentException
                                              or NotSupportedException
                                              or IOException)
        {
            logger.LogWarning(exception,
                "Could not resolve {StagingDirectory} while checking for interrupted removals; skipping it",
                description);
            return null;
        }
    }

    // Installs live one level under the installations directory, legacy installs directly under the root, and export
    // templates under the Godot templates directory.
    private void AddReleaseDirectories(ICollection<string> stagingDirectories, string installationsDirectory)
    {
        if (hostSystem.DirectoryExists(installationsDirectory) is not Result<bool, FileOperationError>.Success
            {
                Value: true
            })
        {
            return;
        }

        switch (hostSystem.EnumerateDirectories(installationsDirectory))
        {
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var releaseDirectories):
                foreach (var releaseDirectory in releaseDirectories)
                {
                    stagingDirectories.Add(releaseDirectory.FullName);
                }

                break;
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var error):
                logger.LogWarning("Could not list {InstallationsDirectory} while checking for interrupted removals: {Error}",
                    installationsDirectory, error);
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }
}
