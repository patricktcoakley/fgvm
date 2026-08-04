using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Environment;

/// <summary>
///     Removes directories by renaming them aside before deleting them, so an interrupted delete never leaves a
///     partially deleted installation that still looks installed.
/// </summary>
public interface IDirectoryRemoval
{
    /// <summary>
    ///     Renames each existing directory aside, into its own parent so the rename never crosses a volume.
    /// </summary>
    /// <param name="directoryPaths">The directories to remove.</param>
    /// <returns>The staged paths, ready for <see cref="Discard" />.</returns>
    Result<IReadOnlyList<string>, FileOperationError> Stage(IEnumerable<string> directoryPaths);

    /// <summary>
    ///     Deletes staged directories. Failures are logged and left for the next <see cref="Sweep" />.
    /// </summary>
    /// <param name="stagedPaths">Paths returned by <see cref="Stage" />.</param>
    void Discard(IEnumerable<string> stagedPaths);

    /// <summary>
    ///     Deletes marker directories left behind by an interrupted removal, install, or template extraction. Never
    ///     throws, so it is safe to call on startup.
    /// </summary>
    /// <param name="parentDirectories">Directories that removals and commits stage into.</param>
    void Sweep(IEnumerable<string> parentDirectories);
}

public sealed class DirectoryRemoval(
    IHostSystem hostSystem,
    ILogger<DirectoryRemoval> logger,
    TimeProvider timeProvider
) : IDirectoryRemoval
{
    // Unlike a tombstone, staging and backup directories may belong to an install running in another process, and
    // Sweep runs on every command. Nothing takes a day, so waiting this long removes that race entirely.
    private static readonly TimeSpan StaleMarkerAge = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public Result<IReadOnlyList<string>, FileOperationError> Stage(IEnumerable<string> directoryPaths)
    {
        var staged = new List<string>();
        foreach (var directoryPath in directoryPaths.Select(Path.GetFullPath))
        {
            // Check after every move so the host filesystem decides whether two path spellings refer to the same
            // directory. This handles aliases on case-insensitive volumes without collapsing distinct paths on a
            // case-sensitive volume.
            if (!DirectoryExists(directoryPath))
            {
                continue;
            }

            var tombstonePath = StagedDirectoryNames.CreateTombstonePath(directoryPath);
            switch (hostSystem.MoveDirectory(directoryPath, tombstonePath))
            {
                case Result<Unit, FileOperationError>.Success:
                    staged.Add(tombstonePath);
                    break;
                case Result<Unit, FileOperationError>.Failure(var error):
                    // Anything already staged stays staged; it's a tombstone the next sweep collects
                    return new Result<IReadOnlyList<string>, FileOperationError>.Failure(error);
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }

        return new Result<IReadOnlyList<string>, FileOperationError>.Success(staged);
    }

    /// <inheritdoc />
    public void Discard(IEnumerable<string> stagedPaths)
    {
        foreach (var stagedPath in stagedPaths)
        {
            Delete(stagedPath);
        }
    }

    /// <inheritdoc />
    public void Sweep(IEnumerable<string> parentDirectories)
    {
        foreach (var parentDirectory in parentDirectories)
        {
            if (!DirectoryExists(parentDirectory))
            {
                continue;
            }

            switch (hostSystem.EnumerateDirectories(parentDirectory))
            {
                case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                    foreach (var entry in entries)
                    {
                        if (ShouldDelete(entry))
                        {
                            Delete(entry.FullName);
                        }
                    }

                    break;
                case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var error):
                    logger.LogWarning("Could not check {ParentDirectory} for interrupted removals: {Error}",
                        parentDirectory, error);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }
    }

    private bool DirectoryExists(string path) =>
        hostSystem.DirectoryExists(path) is Result<bool, FileOperationError>.Success { Value: true };

    private void Delete(string tombstonePath)
    {
        if (hostSystem.DeleteDirectoryIfExists(tombstonePath, true) is
            Result<Unit, FileOperationError>.Failure(var error))
        {
            logger.LogWarning("Could not delete {TombstonePath}: {Error}; it will be retried later", tombstonePath, error);
        }
    }

    private bool ShouldDelete(HostDirectoryEntry entry)
    {
        if (!StagedDirectoryNames.TryClassify(entry.Name, out var kind))
        {
            return false;
        }

        if (kind is StagedDirectoryKind.Tombstone)
        {
            return true;
        }

        return timeProvider.GetUtcNow() - entry.LastWriteTimeUtc >= StaleMarkerAge;
    }
}
