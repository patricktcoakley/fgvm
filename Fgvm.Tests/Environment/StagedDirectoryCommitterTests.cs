using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Environment;

public sealed class StagedDirectoryCommitterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-staged-commit-tests", Guid.NewGuid().ToString("N"));

    private readonly IHostSystem _hostSystem;

    public StagedDirectoryCommitterTests()
    {
        Directory.CreateDirectory(_root);
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(_root);
        _hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);
    }

    [Fact]
    public void Commit_MovesStagedDirectoryIntoMissingDestination()
    {
        var staged = CreateDirectoryWithFile("staged", "file.txt", "new");
        var destination = Path.Combine(_root, "destination");

        var result = StagedDirectoryCommitter.Commit(_hostSystem, staged, destination, overwrite: false, NullLogger.Instance);

        Assert.IsType<Result<Unit, DirectoryCommitError>.Success>(result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "file.txt")));
        Assert.False(Directory.Exists(staged));
    }

    [Fact]
    public void Commit_ReturnsDestinationExists_WithoutTouchingAnything_WhenNotOverwriting()
    {
        var staged = CreateDirectoryWithFile("staged", "file.txt", "new");
        var destination = CreateDirectoryWithFile("destination", "file.txt", "old");

        var result = StagedDirectoryCommitter.Commit(_hostSystem, staged, destination, overwrite: false, NullLogger.Instance);

        var failure = Assert.IsType<Result<Unit, DirectoryCommitError>.Failure>(result);
        Assert.IsType<DirectoryCommitError.DestinationExists>(failure.Error);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "file.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(staged, "file.txt")));
        Assert.Empty(BackupDirectories());
    }

    [Fact]
    public void Commit_ReplacesExistingDestination_WhenOverwriting()
    {
        var staged = CreateDirectoryWithFile("staged", "file.txt", "new");
        var destination = CreateDirectoryWithFile("destination", "old.txt", "old");

        var result = StagedDirectoryCommitter.Commit(_hostSystem, staged, destination, overwrite: true, NullLogger.Instance);

        Assert.IsType<Result<Unit, DirectoryCommitError>.Success>(result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "file.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
        Assert.Empty(BackupDirectories());
    }

    [Fact]
    public void Commit_KeepsNewInstall_WhenBackupCleanupFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var staged = CreateDirectoryWithFile("staged", "file.txt", "new");
        var destination = CreateDirectoryWithFile("destination", "old.txt", "old");
        // A directory without write permission cannot have its children unlinked, so deleting the backup fails.
        var locked = Path.Combine(destination, "locked");
        CreateDirectoryWithFile(Path.Combine("destination", "locked"), "pinned.txt", "pinned");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var result = StagedDirectoryCommitter.Commit(_hostSystem, staged, destination, overwrite: true, NullLogger.Instance);

            Assert.IsType<Result<Unit, DirectoryCommitError>.Success>(result);
            Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "file.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
            // The old version could not be fully removed, but the commit must not have been rolled back.
            var backup = Assert.Single(BackupDirectories());
            // Dot-prefixed, so what it leaves behind is skipped by the registry scans rather than listed as an install.
            Assert.StartsWith(".backup-", Path.GetFileName(backup), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var backup in BackupDirectories())
            {
                File.SetUnixFileMode(Path.Combine(backup, "locked"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public void Commit_RestoresExistingDestination_WhenStagedMoveFails()
    {
        var missingStaged = Path.Combine(_root, "staged-does-not-exist");
        var destination = CreateDirectoryWithFile("destination", "file.txt", "old");

        var result = StagedDirectoryCommitter.Commit(_hostSystem, missingStaged, destination, overwrite: true, NullLogger.Instance);

        var failure = Assert.IsType<Result<Unit, DirectoryCommitError>.Failure>(result);
        Assert.IsType<DirectoryCommitError.CommitFailed>(failure.Error);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "file.txt")));
        Assert.Empty(BackupDirectories());
    }

    [Fact]
    public void RestoreBackup_SwallowsFailure_WhenBackupAndDestinationBothMissing()
    {
        var backupPath = Path.Combine(_root, "missing-backup");
        var destinationPath = Path.Combine(_root, "missing-destination");

        var exception = Record.Exception(() => StagedDirectoryCommitter.RestoreBackup(_hostSystem, backupPath, destinationPath, NullLogger.Instance));

        Assert.Null(exception);
        Assert.False(Directory.Exists(destinationPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private string CreateDirectoryWithFile(string relativeDirectory, string fileName, string content)
    {
        var directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        return directory;
    }

    private string[] BackupDirectories() =>
        Directory.GetDirectories(_root, "*.backup-*");
}
