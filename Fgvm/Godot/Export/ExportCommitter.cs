using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot.Export;

internal sealed record StagedExportArtifact(
    string StagedPath,
    string DestinationPath,
    ExportArtifactKind Kind
);

internal sealed record ExportCommitError(string Path, string Reason);

/// <summary>
///     Promotes a fully staged export run and rolls earlier promotions back when a later move fails.
/// </summary>
internal static class ExportCommitter
{
    internal static Result<Unit, ExportCommitError> Commit(IHostSystem hostSystem,
        IDirectoryRemoval directoryRemoval,
        IReadOnlyList<StagedExportArtifact> artifacts,
        ILogger logger
    )
    {
        if (Preflight(hostSystem, artifacts) is { } preflightError)
        {
            return Failed(preflightError);
        }

        var states = new List<CommitState>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            var state = new CommitState(artifact, StagedDirectoryNames.CreateBackupPath(artifact.DestinationPath));
            states.Add(state);

            if (Backup(hostSystem, directoryRemoval, state) is { } backupError)
            {
                RollBack(hostSystem, directoryRemoval, states, logger);
                return Failed(backupError);
            }

            if (Promote(hostSystem, state) is { } promoteError)
            {
                RollBack(hostSystem, directoryRemoval, states, logger);
                return Failed(promoteError);
            }
        }

        directoryRemoval.Discard(states.Where(state => state.HasBackup).Select(state => state.BackupPath));

        return new Result<Unit, ExportCommitError>.Success(Unit.Value);
    }

    private static ExportCommitError? Preflight(IHostSystem hostSystem,
        IReadOnlyList<StagedExportArtifact> artifacts
    )
    {
        foreach (var artifact in artifacts)
        {
            var wrongKind = artifact.Kind switch
            {
                ExportArtifactKind.Directory => hostSystem.FileExists(artifact.DestinationPath),
                ExportArtifactKind.File => hostSystem.DirectoryExists(artifact.DestinationPath),
                _ => throw new InvalidOperationException("Unexpected export artifact kind.")
            };

            if (wrongKind is Result<bool, FileOperationError>.Failure(var error))
            {
                return new ExportCommitError(artifact.DestinationPath, error.ToString());
            }

            if (wrongKind is Result<bool, FileOperationError>.Success(true))
            {
                return new ExportCommitError(
                    artifact.DestinationPath,
                    "The existing destination has a different filesystem kind.");
            }
        }

        return null;
    }

    private static ExportCommitError? Backup(IHostSystem hostSystem,
        IDirectoryRemoval directoryRemoval,
        CommitState state
    )
    {
        var destination = state.Artifact.DestinationPath;
        var exists = state.Artifact.Kind switch
        {
            ExportArtifactKind.Directory => hostSystem.DirectoryExists(destination),
            ExportArtifactKind.File => hostSystem.FileExists(destination),
            _ => throw new InvalidOperationException("Unexpected export artifact kind.")
        };

        if (exists is Result<bool, FileOperationError>.Failure(var inspectionError))
        {
            return new ExportCommitError(destination, inspectionError.ToString());
        }

        if (exists is not Result<bool, FileOperationError>.Success(true))
        {
            return null;
        }

        Result<Unit, FileOperationError> result;
        if (state.Artifact.Kind is ExportArtifactKind.Directory)
        {
            result = hostSystem.MoveDirectory(destination, state.BackupPath);
        }
        else
        {
            if (hostSystem.CreateDirectory(state.BackupPath) is Result<Unit, FileOperationError>.Failure(var createError))
            {
                return new ExportCommitError(destination, createError.ToString());
            }

            result = hostSystem.MoveFile(
                destination,
                Path.Combine(state.BackupPath, Path.GetFileName(destination)),
                false);
        }

        if (result is Result<Unit, FileOperationError>.Failure(var error))
        {
            if (state.Artifact.Kind is ExportArtifactKind.File)
            {
                directoryRemoval.Discard([state.BackupPath]);
            }

            return new ExportCommitError(destination, error.ToString());
        }

        state.HasBackup = true;
        return null;
    }

    private static ExportCommitError? Promote(IHostSystem hostSystem, CommitState state)
    {
        var artifact = state.Artifact;
        var result = artifact.Kind switch
        {
            ExportArtifactKind.Directory => hostSystem.MoveDirectory(artifact.StagedPath, artifact.DestinationPath),
            ExportArtifactKind.File => hostSystem.MoveFile(artifact.StagedPath, artifact.DestinationPath, false),
            _ => throw new InvalidOperationException("Unexpected export artifact kind.")
        };

        if (result is Result<Unit, FileOperationError>.Failure(var error))
        {
            return new ExportCommitError(artifact.DestinationPath, error.ToString());
        }

        state.Promoted = true;
        return null;
    }

    private static void RollBack(IHostSystem hostSystem,
        IDirectoryRemoval directoryRemoval,
        IReadOnlyList<CommitState> states,
        ILogger logger
    )
    {
        for (var i = states.Count - 1; i >= 0; i--)
        {
            var state = states[i];
            if (state.Promoted && !DeleteDestination(hostSystem, state.Artifact, logger))
            {
                continue;
            }

            if (!state.HasBackup)
            {
                continue;
            }

            var restore = state.Artifact.Kind switch
            {
                ExportArtifactKind.Directory =>
                    hostSystem.MoveDirectory(state.BackupPath, state.Artifact.DestinationPath),
                ExportArtifactKind.File =>
                    hostSystem.MoveFile(
                        Path.Combine(state.BackupPath, Path.GetFileName(state.Artifact.DestinationPath)),
                        state.Artifact.DestinationPath,
                        false),
                _ => throw new InvalidOperationException("Unexpected export artifact kind.")
            };

            if (restore is Result<Unit, FileOperationError>.Failure(var error))
            {
                logger.LogError(
                    "Failed to restore export destination {Destination} from {Backup}: {Error}",
                    state.Artifact.DestinationPath,
                    state.BackupPath,
                    error);
                continue;
            }

            if (state.Artifact.Kind is ExportArtifactKind.File)
            {
                directoryRemoval.Discard([state.BackupPath]);
            }
        }
    }

    private static bool DeleteDestination(IHostSystem hostSystem,
        StagedExportArtifact artifact,
        ILogger logger
    )
    {
        var result = artifact.Kind switch
        {
            ExportArtifactKind.Directory => hostSystem.DeleteDirectoryIfExists(artifact.DestinationPath, true),
            ExportArtifactKind.File => hostSystem.DeleteFileIfExists(artifact.DestinationPath),
            _ => throw new InvalidOperationException("Unexpected export artifact kind.")
        };

        if (result is not Result<Unit, FileOperationError>.Failure(var error))
        {
            return true;
        }

        logger.LogError("Failed to remove new export destination {Destination} during rollback: {Error}",
            artifact.DestinationPath,
            error);
        return false;
    }

    private static Result<Unit, ExportCommitError> Failed(ExportCommitError error) =>
        new Result<Unit, ExportCommitError>.Failure(error);

    private sealed class CommitState(StagedExportArtifact artifact, string backupPath)
    {
        internal StagedExportArtifact Artifact { get; } = artifact;
        internal string BackupPath { get; } = backupPath;
        internal bool HasBackup { get; set; }
        internal bool Promoted { get; set; }
    }
}
