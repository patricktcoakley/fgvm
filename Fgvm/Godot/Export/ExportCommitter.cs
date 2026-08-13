using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot.Export;

internal sealed record StagedExportArtifact(
    string StagedPath,
    string DestinationPath,
    ExportArtifactKind Kind,
    bool OwnsDestination
);

internal sealed record ExportCommitError(string Path, string Reason);

/// <summary>
///     Promotes a fully staged export run and rolls earlier promotions back when a later move fails.
/// </summary>
internal static class ExportCommitter
{
    private const string RescuedPrefix = "fgvm-rescued-";

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
                return Failed(backupError, RollBack(hostSystem, directoryRemoval, states, logger));
            }

            if (Promote(hostSystem, state) is { } promoteError)
            {
                return Failed(promoteError, RollBack(hostSystem, directoryRemoval, states, logger));
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

    private static bool IsMerged(StagedExportArtifact artifact) =>
        artifact.Kind is ExportArtifactKind.Directory && !artifact.OwnsDestination;

    private static ExportCommitError? Backup(IHostSystem hostSystem,
        IDirectoryRemoval directoryRemoval,
        CommitState state
    ) =>
        IsMerged(state.Artifact)
            ? null
            : BackupWholeDestination(hostSystem, directoryRemoval, state);

    private static ExportCommitError? BackupWholeDestination(IHostSystem hostSystem,
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

    private static ExportCommitError? Promote(IHostSystem hostSystem, CommitState state) =>
        IsMerged(state.Artifact)
            ? MergeFiles(hostSystem, state)
            : PromoteWholeArtifact(hostSystem, state);

    private static ExportCommitError? MergeFiles(IHostSystem hostSystem, CommitState state)
    {
        var staged = new List<string>();
        if (CollectFiles(hostSystem, state.Artifact.StagedPath, string.Empty, staged) is { } collectError)
        {
            return collectError;
        }

        staged.Sort(StringComparer.Ordinal);

        var written = 0;
        foreach (var relative in staged)
        {
            if (EnsureDirectoryChain(hostSystem, state, Path.GetDirectoryName(relative) ?? string.Empty) is { } directoryError)
            {
                return Partial(directoryError, state, written);
            }

            var target = Path.Combine(state.Artifact.DestinationPath, relative);
            if (hostSystem.MoveFile(Path.Combine(state.Artifact.StagedPath, relative), target, true) is
                Result<Unit, FileOperationError>.Failure(var error))
            {
                return Partial(new ExportCommitError(target, error.ToString()), state, written);
            }

            written++;
            state.Promoted = true;
        }

        state.Promoted = true;
        return null;
    }

    private static ExportCommitError Partial(ExportCommitError error, CommitState state, int written) =>
        written == 0
            ? error
            : error with
            {
                Reason = $"{error.Reason} {written} file(s) had already been written to " +
                         $"'{state.Artifact.DestinationPath}', which now holds a mix of this run and the previous one."
            };

    private static ExportCommitError? CollectFiles(IHostSystem hostSystem,
        string absolute,
        string relative,
        List<string> files
    )
    {
        switch (hostSystem.EnumerateEntries(absolute))
        {
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var error):
                return new ExportCommitError(absolute, error.ToString());
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                foreach (var entry in entries)
                {
                    var childRelative = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
                    var isRealDirectory = entry.Attributes.HasFlag(FileAttributes.Directory) &&
                                          !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

                    if (!isRealDirectory)
                    {
                        files.Add(childRelative);
                        continue;
                    }

                    if (CollectFiles(hostSystem, entry.FullName, childRelative, files) is { } childError)
                    {
                        return childError;
                    }
                }

                return null;
            default:
                throw new InvalidOperationException("Unexpected enumeration result type.");
        }
    }

    private static ExportCommitError? EnsureDirectoryChain(IHostSystem hostSystem, CommitState state, string relativeDirectory)
    {
        var current = state.Artifact.DestinationPath;
        var segments = relativeDirectory.Length == 0
            ? []
            : relativeDirectory.Split(Path.DirectorySeparatorChar);

        for (var index = -1; index < segments.Length; index++)
        {
            if (index >= 0)
            {
                current = Path.Combine(current, segments[index]);
            }

            switch (hostSystem.DirectoryExists(current))
            {
                case Result<bool, FileOperationError>.Failure(var error):
                    return new ExportCommitError(current, error.ToString());
                case Result<bool, FileOperationError>.Success(true):
                    if (hostSystem.ResolveLinkTarget(current, false) is
                        Result<string?, FileOperationError>.Success(not null))
                    {
                        return new ExportCommitError(
                            current,
                            "The export path passes through a symbolic link, so writing it would modify content outside the destination.");
                    }

                    continue;
            }

            if (hostSystem.CreateDirectory(current) is Result<Unit, FileOperationError>.Failure(var createError))
            {
                return new ExportCommitError(current, createError.ToString());
            }
        }

        return null;
    }

    private static ExportCommitError? PromoteWholeArtifact(IHostSystem hostSystem, CommitState state)
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

    private static List<string> RollBack(IHostSystem hostSystem,
        IDirectoryRemoval directoryRemoval,
        IReadOnlyList<CommitState> states,
        ILogger logger
    )
    {
        var rescued = new List<string>();

        for (var i = states.Count - 1; i >= 0; i--)
        {
            var state = states[i];

            if (IsMerged(state.Artifact) || !state.HasBackup && !state.Promoted)
            {
                continue;
            }

            if (state.Promoted && !DeleteDestination(hostSystem, state.Artifact, logger))
            {
                if (!Rescue(hostSystem, state, logger, rescued))
                {
                    rescued.Add($"'{state.Artifact.DestinationPath}' still holds output from the failed run");
                }

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
                Rescue(hostSystem, state, logger, rescued);
                continue;
            }

            if (state.Artifact.Kind is ExportArtifactKind.File)
            {
                directoryRemoval.Discard([state.BackupPath]);
            }
        }

        return rescued;
    }

    private static bool Rescue(IHostSystem hostSystem, CommitState state, ILogger logger, List<string> rescued)
    {
        if (!state.HasBackup || Path.GetDirectoryName(state.BackupPath) is not { } parent)
        {
            return false;
        }

        var target = Path.Combine(parent, RescuedPrefix + Guid.NewGuid().ToString("N"));
        if (hostSystem.MoveDirectory(state.BackupPath, target) is Result<Unit, FileOperationError>.Failure(var error))
        {
            logger.LogError("Failed to preserve unrestored export content at {Backup}: {Error}", state.BackupPath, error);
            rescued.Add($"the previous contents remain at '{state.BackupPath}', which fgvm may clean up after 24 hours — move them now");
            return true;
        }

        logger.LogError("Export content that could not be restored was preserved at {Rescued}.", target);
        rescued.Add($"the previous contents were preserved at '{target}'");
        return true;
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

    private static Result<Unit, ExportCommitError> Failed(ExportCommitError error, IReadOnlyList<string> rescued) =>
        rescued is []
            ? Failed(error)
            : Failed(error with { Reason = $"{error.Reason} Also: {string.Join("; ", rescued)}." });

    private sealed class CommitState(StagedExportArtifact artifact, string backupPath)
    {
        internal StagedExportArtifact Artifact { get; } = artifact;
        internal string BackupPath { get; } = backupPath;
        internal bool HasBackup { get; set; }
        internal bool Promoted { get; set; }
    }
}
