using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Environment;

public sealed class DirectoryRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-directory-removal-tests",
        Guid.NewGuid().ToString("N"));

    private readonly DirectoryRemoval _removal;

    public DirectoryRemovalTests()
    {
        Directory.CreateDirectory(_root);
        _removal = new DirectoryRemoval(CreateHostSystem(), NullLogger<DirectoryRemoval>.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Stage_HidesTheDirectoryImmediately_AndDiscardDeletesIt()
    {
        var installationPath = CreateDirectory("editor");

        var staged = AssertStaged(_removal.Stage([installationPath]));

        Assert.False(Directory.Exists(installationPath));
        Assert.Single(staged);
        Assert.True(Directory.Exists(staged[0]));

        _removal.Discard(staged);

        Assert.False(Directory.Exists(staged[0]));
        Assert.Empty(Tombstones());
    }

    [Fact]
    public void Stage_KeepsTheTombstoneInTheSameParent_SoTheRenameNeverCrossesAVolume()
    {
        var installationPath = CreateDirectory("editor");

        var staged = AssertStaged(_removal.Stage([installationPath]));

        Assert.Equal(Path.GetDirectoryName(installationPath), Path.GetDirectoryName(staged[0]));
    }

    [Fact]
    public void Stage_UsesAnFgvmOwnedTombstoneName()
    {
        var installationPath = CreateDirectory("editor");

        var staged = AssertStaged(_removal.Stage([installationPath]));

        var tombstoneName = Path.GetFileName(Assert.Single(staged));
        const string prefix = ".fgvm-removing-";
        Assert.StartsWith(prefix, tombstoneName, StringComparison.Ordinal);

        var marker = tombstoneName[prefix.Length..];
        Assert.True(marker.Length > 33);
        Assert.True(Guid.TryParseExact(marker[..32], "N", out _));
        Assert.Equal('-', marker[32]);
        Assert.Equal("editor", marker[33..]);
    }

    [Fact]
    public void Stage_SkipsMissingDirectoriesAndDeduplicates()
    {
        var installationPath = CreateDirectory("editor");

        var staged = AssertStaged(_removal.Stage(
            [installationPath, installationPath, Path.Combine(_root, "not-there")]));

        Assert.Single(staged);
    }

    [Fact]
    public void Stage_DoesNotStageTheSameDirectoryTwice_WhenPathsDifferOnlyByCase()
    {
        var installationPath = CreateDirectory("editor");
        var otherCasing = Path.Combine(_root, "EDITOR");

        var staged = AssertStaged(_removal.Stage([installationPath, otherCasing]));

        Assert.Single(staged);
        Assert.Single(Tombstones());
    }

    [Fact]
    public void Stage_StagesBothDirectories_WhenTheHostTreatsCaseVariantsAsDistinct()
    {
        var lowercasePath = Path.Combine(_root, "editor");
        var uppercasePath = Path.Combine(_root, "EDITOR");
        var existingDirectories = new HashSet<string>([lowercasePath, uppercasePath], StringComparer.Ordinal);
        var hostSystem = new Mock<IHostSystem>(MockBehavior.Strict);
        hostSystem.Setup(x => x.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => new Result<bool, FileOperationError>.Success(existingDirectories.Contains(path)));
        hostSystem.Setup(x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string source, string destination) =>
            {
                if (!existingDirectories.Remove(source))
                {
                    return new Result<Unit, FileOperationError>.Failure(new FileOperationError.NotFound(source));
                }

                existingDirectories.Add(destination);
                return new Result<Unit, FileOperationError>.Success(Unit.Value);
            });

        var removal = new DirectoryRemoval(hostSystem.Object, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System);

        var staged = AssertStaged(removal.Stage([lowercasePath, uppercasePath]));

        Assert.Equal(2, staged.Count);
        hostSystem.Verify(x => x.MoveDirectory(lowercasePath, It.IsAny<string>()), Times.Once);
        hostSystem.Verify(x => x.MoveDirectory(uppercasePath, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Stage_LeavesEarlierDirectoriesStagedForSweeping_WhenALaterOneCannotBeMoved()
    {
        var first = CreateDirectory("editor");
        var second = CreateDirectory("templates");
        var removal = new DirectoryRemoval(
            CreateHostSystem(failMoveFrom: second),
            NullLogger<DirectoryRemoval>.Instance, TimeProvider.System);

        var result = removal.Stage([first, second]);

        Assert.IsType<Result<IReadOnlyList<string>, FileOperationError>.Failure>(result);

        // The first is already staged; it's a tombstone, not a half-deleted installation
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Single(Tombstones());

        _removal.Sweep([_root]);
        Assert.Empty(Tombstones());
    }

    [Fact]
    public void Sweep_DeletesWhatAnInterruptedRemovalLeftBehind()
    {
        var installationPath = CreateDirectory("editor");
        var staged = AssertStaged(_removal.Stage([installationPath]));

        // Stand in for a process that died between the rename and the delete
        _removal.Sweep([_root]);

        Assert.False(Directory.Exists(staged[0]));
        Assert.Empty(Tombstones());
    }

    [Fact]
    public void Sweep_DeletesATombstoneThatWasOnlyPartlyDeleted()
    {
        var installationPath = CreateDirectory("editor");
        Directory.CreateDirectory(Path.Combine(installationPath, "nested"));
        File.WriteAllText(Path.Combine(installationPath, "nested", "godot"), "binary");
        var staged = AssertStaged(_removal.Stage([installationPath]));

        // Stand in for a process that died partway through the recursive delete
        File.Delete(Path.Combine(staged[0], "contents.txt"));

        _removal.Sweep([_root]);

        Assert.Empty(Tombstones());
    }

    [Fact]
    public void Sweep_LeavesRealInstallationsAlone()
    {
        var installationPath = CreateDirectory("editor");
        var dotDirectory = CreateDirectory(".config");

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(installationPath));
        Assert.True(Directory.Exists(dotDirectory));
    }

    [Fact]
    public void Sweep_LeavesDirectoriesWithTheGenericRemovingPrefixAlone()
    {
        var unrelatedDirectory = CreateDirectory(".removing-backup");

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(unrelatedDirectory));
    }

    [Theory]
    [InlineData(".fgvm-removing-backup")]
    [InlineData(".fgvm-removing-0000000000000000000000000000000z-editor")]
    [InlineData(".fgvm-removing-00000000000000000000000000000000")]
    public void Sweep_LeavesMalformedFgvmTombstoneNamesAlone(string directoryName)
    {
        var unrelatedDirectory = CreateDirectory(directoryName);

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(unrelatedDirectory));
    }

    [Fact]
    public void Sweep_IgnoresMissingDirectoriesAndDoesNotThrow()
    {
        _removal.Sweep([Path.Combine(_root, "not-there"), _root]);
    }

    [Fact]
    public void Sweep_DoesNotThrow_AndKeepsTheTombstone_WhenTheDeleteFails()
    {
        var installationPath = CreateDirectory("editor");
        var staged = AssertStaged(_removal.Stage([installationPath]));
        var removal = new DirectoryRemoval(
            CreateHostSystem(failDeleteOf: staged[0]),
            NullLogger<DirectoryRemoval>.Instance, TimeProvider.System);

        // Sweep runs on startup; throwing here would take down whatever command the user ran
        removal.Sweep([_root]);

        Assert.True(Directory.Exists(staged[0]));

        _removal.Sweep([_root]);
        Assert.Empty(Tombstones());
    }

    [Fact]
    public void Sweep_DoesNotThrow_WhenAParentDirectoryCannotBeRead()
    {
        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(x => x.DirectoryExists(It.IsAny<string>()))
            .Returns(new Result<bool, FileOperationError>.Success(true));
        hostSystem.Setup(x => x.EnumerateDirectories(It.IsAny<string>()))
            .Returns(new Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(
                new FileOperationError.PermissionDenied(_root)));

        new DirectoryRemoval(hostSystem.Object, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System).Sweep([_root]);
    }

    [Fact]
    public void Discard_DoesNotThrow_AndKeepsTheTombstone_WhenTheDeleteFails()
    {
        var installationPath = CreateDirectory("editor");
        var staged = AssertStaged(_removal.Stage([installationPath]));
        var removal = new DirectoryRemoval(
            CreateHostSystem(failDeleteOf: staged[0]),
            NullLogger<DirectoryRemoval>.Instance, TimeProvider.System);

        // The removal already happened; a failed delete waits for the next sweep
        removal.Discard(staged);

        Assert.True(Directory.Exists(staged[0]));

        _removal.Discard(staged);
        Assert.False(Directory.Exists(staged[0]));
    }

    [Fact]
    public void Discard_DoesNotThrow_WhenSomethingElseAlreadyDeletedTheTombstone()
    {
        var installationPath = CreateDirectory("editor");
        var staged = AssertStaged(_removal.Stage([installationPath]));
        Directory.Delete(staged[0], recursive: true);

        // Two processes can sweep at once; both agree the tombstone is going away
        _removal.Discard(staged);
    }

    // Real host system so renames and deletes are exercised for real, with failure injection where provoking one
    // otherwise needs permission tricks
    private IHostSystem CreateHostSystem(string? failMoveFrom = null, string? failDeleteOf = null)
    {
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(_root);
        var real = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);

        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(x => x.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => real.DirectoryExists(path));
        hostSystem.Setup(x => x.EnumerateDirectories(It.IsAny<string>()))
            .Returns((string path) => real.EnumerateDirectories(path));
        hostSystem.Setup(x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string source, string destination) =>
                source == failMoveFrom
                    ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.PermissionDenied(destination))
                    : real.MoveDirectory(source, destination));
        hostSystem.Setup(x => x.DeleteDirectoryIfExists(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool recursive) =>
                path == failDeleteOf
                    ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.PermissionDenied(path))
                    : real.DeleteDirectoryIfExists(path, recursive));
        return hostSystem.Object;
    }

    private static IReadOnlyList<string> AssertStaged(Result<IReadOnlyList<string>, FileOperationError> result) =>
        Assert.IsType<Result<IReadOnlyList<string>, FileOperationError>.Success>(result).Value;

    private string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "contents.txt"), "contents");
        return path;
    }

    private string[] Tombstones() => Directory.GetDirectories(_root, ".*removing-*");

    private DirectoryRemoval CreateRemovalAt(DateTimeOffset now) =>
        new(CreateHostSystem(), NullLogger<DirectoryRemoval>.Instance, new StubTimeProvider(now));

    private string CreateMarker(string name, TimeSpan age)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "contents.txt"), "contents");
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void Sweep_LeavesARecentStagingDirectoryAlone()
    {
        var staging = CreateMarker($".fgvm-staging-{Guid.NewGuid():N}", TimeSpan.FromMinutes(5));

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void Sweep_LeavesARecentBackupAlone()
    {
        var backup = CreateMarker($".backup-{Guid.NewGuid():N}-editor", TimeSpan.FromMinutes(5));

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void Sweep_DeletesAStaleStagingDirectory()
    {
        var staging = CreateMarker($".fgvm-staging-{Guid.NewGuid():N}", TimeSpan.FromDays(3));

        _removal.Sweep([_root]);

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Sweep_DeletesAStaleTemplateStagingDirectory()
    {
        var staging = CreateMarker($".fgvm-template-staging-{Guid.NewGuid():N}", TimeSpan.FromDays(3));

        _removal.Sweep([_root]);

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Sweep_DeletesAStaleBackup()
    {
        var backup = CreateMarker($".backup-{Guid.NewGuid():N}-editor", TimeSpan.FromDays(3));

        _removal.Sweep([_root]);

        Assert.False(Directory.Exists(backup));
    }

    // A tombstone's content is already doomed, so age is irrelevant to it.
    [Fact]
    public void Sweep_DeletesAFreshTombstone()
    {
        var installationPath = CreateDirectory("editor");
        var staged = AssertStaged(_removal.Stage([installationPath]));

        _removal.Sweep([_root]);

        Assert.False(Directory.Exists(staged[0]));
    }

    [Fact]
    public void Sweep_NeverTouchesDirectoriesThatAreNotOurs()
    {
        var installation = CreateDirectory("4.3-stable");
        var lookalike = CreateMarker(".backup-notes", TimeSpan.FromDays(30));
        var dotted = CreateMarker(".config", TimeSpan.FromDays(30));

        _removal.Sweep([_root]);

        Assert.True(Directory.Exists(installation));
        Assert.True(Directory.Exists(lookalike));
        Assert.True(Directory.Exists(dotted));
    }

    [Fact]
    public void Sweep_UsesTheClockRatherThanWallTime()
    {
        var staging = CreateMarker($".fgvm-staging-{Guid.NewGuid():N}", TimeSpan.FromHours(1));

        CreateRemovalAt(DateTimeOffset.UtcNow + TimeSpan.FromHours(22)).Sweep([_root]);
        Assert.True(Directory.Exists(staging));

        CreateRemovalAt(DateTimeOffset.UtcNow + TimeSpan.FromHours(25)).Sweep([_root]);
        Assert.False(Directory.Exists(staging));
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
