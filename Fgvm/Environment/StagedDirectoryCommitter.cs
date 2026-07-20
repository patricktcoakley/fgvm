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
    /// <param name="stagedPath">The fully populated staging directory, expected to live beside the destination.</param>
    /// <param name="destinationPath">The final directory to commit to.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <param name="logger">Logger for non-fatal cleanup and rollback problems.</param>
    /// <returns>Success once the destination holds the staged content, or a typed commit error.</returns>
    public static Result<Unit, DirectoryCommitError> Commit(string stagedPath,
        string destinationPath,
        bool overwrite,
        ILogger logger
    )
    {
        var backupPath = $"{destinationPath}.backup-{Guid.NewGuid():N}";
        var backupMoved = false;

        try
        {
            if (Directory.Exists(destinationPath))
            {
                if (!overwrite)
                {
                    return new Result<Unit, DirectoryCommitError>.Failure(
                        new DirectoryCommitError.DestinationExists(destinationPath));
                }

                Directory.Move(destinationPath, backupPath);
                backupMoved = true;
            }

            Directory.Move(stagedPath, destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backupMoved)
            {
                RestoreBackup(backupPath, destinationPath, logger);
            }

            return new Result<Unit, DirectoryCommitError>.Failure(
                new DirectoryCommitError.CommitFailed(destinationPath, ex.Message));
        }

        // The new directory is live at this point; failing to remove the old backup must not undo the commit.
        if (backupMoved)
        {
            try
            {
                Directory.Delete(backupPath, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "Committed {DestinationPath} but could not remove the previous version at {BackupPath}",
                    destinationPath, backupPath);
            }
        }

        return new Result<Unit, DirectoryCommitError>.Success(Unit.Value);
    }

    internal static void RestoreBackup(string backupPath, string destinationPath, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(destinationPath))
            {
                Directory.Move(backupPath, destinationPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swallow so the original commit failure is what the caller sees, not the rollback's.
            logger.LogWarning(ex, "Failed to restore {DestinationPath} from backup {BackupPath} after a commit error",
                destinationPath, backupPath);
        }
    }
}
