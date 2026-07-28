using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Environment;

/// <summary>
///     Commits a fully staged directory into its final destination with a same-volume rename, so the destination is
///     never observable in a half-written state and a previously working destination is never lost to a failed commit.
/// </summary>
public static class StagedDirectoryCommitter
{
    /// <summary>
    ///     Moves <paramref name="stagedPath" /> into <paramref name="destinationPath" />, replacing an existing
    ///     destination when <paramref name="overwrite" /> is set.
    /// </summary>
    /// <param name="hostSystem">Filesystem operations.</param>
    /// <param name="stagedPath">The fully populated staging directory, expected to live beside the destination.</param>
    /// <param name="destinationPath">The final directory to commit to.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <param name="logger">Logger for non-fatal cleanup and rollback problems.</param>
    /// <returns>Success once the destination holds the staged content, or a typed commit error.</returns>
    public static Result<Unit, DirectoryCommitError> Commit(IHostSystem hostSystem,
        string stagedPath,
        string destinationPath,
        bool overwrite,
        ILogger logger
    )
    {
        var backupPath = CreateBackupPath(destinationPath);
        var backupMoved = false;

        if (DirectoryExists(hostSystem, destinationPath))
        {
            if (!overwrite)
            {
                return new Result<Unit, DirectoryCommitError>.Failure(
                    new DirectoryCommitError.DestinationExists(destinationPath));
            }

            switch (hostSystem.MoveDirectory(destinationPath, backupPath))
            {
                case Result<Unit, FileOperationError>.Success:
                    backupMoved = true;
                    break;
                case Result<Unit, FileOperationError>.Failure(var backupError):
                    return new Result<Unit, DirectoryCommitError>.Failure(
                        new DirectoryCommitError.CommitFailed(destinationPath, backupError.ToString()));
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }
        }

        switch (hostSystem.MoveDirectory(stagedPath, destinationPath))
        {
            case Result<Unit, FileOperationError>.Success:
                break;
            case Result<Unit, FileOperationError>.Failure(var commitError):
                if (backupMoved)
                {
                    RestoreBackup(hostSystem, backupPath, destinationPath, logger);
                }

                return new Result<Unit, DirectoryCommitError>.Failure(
                    new DirectoryCommitError.CommitFailed(destinationPath, commitError.ToString()));
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        // The new directory is live at this point; failing to remove the old backup must not undo the commit.
        if (backupMoved &&
            hostSystem.DeleteDirectoryIfExists(backupPath, true) is Result<Unit, FileOperationError>.Failure(var cleanupError))
        {
            logger.LogWarning(
                "Committed {DestinationPath} but could not remove the previous version at {BackupPath}: {Error}",
                destinationPath, backupPath, cleanupError);
        }

        return new Result<Unit, DirectoryCommitError>.Success(Unit.Value);
    }

    internal static void RestoreBackup(IHostSystem hostSystem, string backupPath, string destinationPath, ILogger logger)
    {
        if (DirectoryExists(hostSystem, destinationPath))
        {
            return;
        }

        if (hostSystem.MoveDirectory(backupPath, destinationPath) is Result<Unit, FileOperationError>.Failure(var error))
        {
            // Reported, not returned, so the original commit failure is what the caller sees, not the rollback's.
            logger.LogWarning("Failed to restore {DestinationPath} from backup {BackupPath} after a commit error: {Error}",
                destinationPath, backupPath, error);
        }
    }

    // Kept in the destination's own parent so the backup is a same-volume rename, and dot-prefixed so a hard kill
    // mid-commit leaves something both registries skip rather than a directory they scan as an installation.
    private static string CreateBackupPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath) ??
                         throw new InvalidOperationException($"Destination path `{destinationPath}` has no parent directory.");
        return Path.Combine(parentPath, $".backup-{Guid.NewGuid():N}-{Path.GetFileName(destinationPath)}");
    }

    private static bool DirectoryExists(IHostSystem hostSystem, string path) =>
        hostSystem.DirectoryExists(path) is Result<bool, FileOperationError>.Success { Value: true };
}
