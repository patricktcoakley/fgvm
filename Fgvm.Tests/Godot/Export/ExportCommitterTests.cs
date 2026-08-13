using Fgvm.Environment;
using Fgvm.Godot.Export;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Godot.Export;

public sealed class ExportCommitterTests : IDisposable
{
    private readonly HostSystem _hostSystem;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-export-commit", Guid.NewGuid().ToString("N"));

    public ExportCommitterTests()
    {
        Directory.CreateDirectory(_root);
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(service => service.RootPath).Returns(_root);
        _hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);
    }

    [Fact]
    public void Commit_ReplacesEveryDestinationOnlyAfterEverythingIsStaged()
    {
        var first = Artifact("first", "new-first", "old-first");
        var second = Artifact("second", "new-second", "old-second");

        var result = Commit(_hostSystem, [first, second]);

        Assert.IsType<Result<Unit, ExportCommitError>.Success>(result);
        Assert.Equal("new-first", Read(first.DestinationPath));
        Assert.Equal("new-second", Read(second.DestinationPath));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WhenALaterPromotionFails_RestoresEveryPriorDestination()
    {
        var first = Artifact("first", "new-first", "old-first");
        var second = Artifact("second", "new-second", "old-second");
        var hostSystem = DelegatingHostThatFailsMove(second.StagedPath);

        var result = Commit(hostSystem, [first, second]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("old-first", Read(first.DestinationPath));
        Assert.Equal("old-second", Read(second.DestinationPath));
        Assert.True(Directory.Exists(second.StagedPath));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WhenALaterPromotionFails_RestoresAPriorFileDestination()
    {
        var stagedFile = FileWithContent("staged.zip", "new-file");
        var destinationFile = FileWithContent("release.zip", "old-file");
        var first = new StagedExportArtifact(stagedFile, destinationFile, ExportArtifactKind.File, true);
        var second = Artifact("second", "new-second", "old-second");
        var hostSystem = DelegatingHostThatFailsMove(second.StagedPath);

        var result = Commit(hostSystem, [first, second]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("old-file", File.ReadAllText(destinationFile));
        Assert.Equal("old-second", Read(second.DestinationPath));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WhenAFileBackupFails_RemovesTheEmptyBackupDirectory()
    {
        var stagedFile = FileWithContent("staged.zip", "new-file");
        var destinationFile = FileWithContent("release.zip", "old-file");
        var artifact = new StagedExportArtifact(stagedFile, destinationFile, ExportArtifactKind.File, true);
        var hostSystem = DelegatingHostThatFailsMove(fileSource: destinationFile);

        var result = Commit(hostSystem, [artifact]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("old-file", File.ReadAllText(destinationFile));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WithADestinationFgvmDoesNotOwn_PreservesUnrelatedContent()
    {
        var artifact = Artifact("release", "new-release", "old-release", ownsDestination: false);
        var unrelatedFile = Path.Combine(artifact.DestinationPath, "signing.config");
        var unrelatedDirectory = Path.Combine(artifact.DestinationPath, "installer");
        File.WriteAllText(unrelatedFile, "keep me");
        Directory.CreateDirectory(unrelatedDirectory);
        File.WriteAllText(Path.Combine(unrelatedDirectory, "setup.iss"), "keep me too");

        var result = Commit(_hostSystem, [artifact]);

        Assert.IsType<Result<Unit, ExportCommitError>.Success>(result);
        Assert.Equal("new-release", Read(artifact.DestinationPath));
        Assert.True(File.Exists(unrelatedFile), "An unrelated file in the destination must survive the commit.");
        Assert.True(
            File.Exists(Path.Combine(unrelatedDirectory, "setup.iss")),
            "An unrelated subdirectory in the destination must survive the commit.");
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WithACollidingSubdirectory_PreservesUnrelatedContentInsideIt()
    {
        var staged = Path.Combine(_root, "staged-nested");
        Directory.CreateDirectory(Path.Combine(staged, "data"));
        File.WriteAllText(Path.Combine(staged, "data", "new.pck"), "new");

        var destination = Path.Combine(_root, "nested");
        Directory.CreateDirectory(Path.Combine(destination, "data"));
        File.WriteAllText(Path.Combine(destination, "data", "user-asset.bin"), "irreplaceable");

        var result = Commit(_hostSystem, [new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory, false)]);

        Assert.IsType<Result<Unit, ExportCommitError>.Success>(result);
        Assert.True(File.Exists(Path.Combine(destination, "data", "new.pck")));
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(destination, "data", "user-asset.bin")));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WithASecondMergeIntoTheSameDestination_ReplacesOnlyItsOwnOutput()
    {
        var destination = Path.Combine(_root, "repeat");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "signing.config"), "keep me");

        Commit(_hostSystem, [new StagedExportArtifact(DirectoryWithFile("first", "v1"), destination, ExportArtifactKind.Directory, false)]);
        var result = Commit(_hostSystem,
            [new StagedExportArtifact(DirectoryWithFile("second", "v2"), destination, ExportArtifactKind.Directory, false)]);

        Assert.IsType<Result<Unit, ExportCommitError>.Success>(result);
        Assert.Equal("v2", Read(destination));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(destination, "signing.config")));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    [Fact]
    public void Commit_WhenAMergedFileCollidesWithADirectory_FailsInsteadOfDeletingIt()
    {
        var staged = DirectoryWithFile("staged-conflict", "new");
        var destination = Path.Combine(_root, "conflict");
        Directory.CreateDirectory(Path.Combine(destination, "game.txt"));
        File.WriteAllText(Path.Combine(destination, "game.txt", "user-notes.md"), "irreplaceable");

        var result = Commit(_hostSystem, [new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory, false)]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(destination, "game.txt", "user-notes.md")));
    }

    [Fact]
    public void Commit_WhenAnOwnedDestinationCannotBeRemoved_PreservesTheBackupOutsideTheSweep()
    {
        var first = Artifact("first", "new-first", "old-first");
        var second = Artifact("second", "new-second", "old-second");
        var hostSystem = DelegatingHostThatFailsMove(
            second.StagedPath,
            deleteDirectoryFailure: first.DestinationPath);

        var result = Commit(hostSystem, [first, second]);

        var failure = Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
        var rescued = Assert.Single(Directory.GetDirectories(_root, "fgvm-rescued-*"));
        Assert.Equal("old-first", Read(rescued));
        Assert.Contains("preserved at", failure.Error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_WhenAMergeFailsPartway_ReportsThatTheDestinationIsMixed()
    {
        var staged = Path.Combine(_root, "staged-partial");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "a.txt"), "new-a");
        File.WriteAllText(Path.Combine(staged, "b.txt"), "new-b");
        var destination = Path.Combine(_root, "partial");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "a.txt"), "old-a");
        File.WriteAllText(Path.Combine(destination, "b.txt"), "old-b");
        var hostSystem = DelegatingHostThatFailsMove(fileSource: Path.Combine(staged, "b.txt"));

        var result = Commit(hostSystem, [new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory, false)]);

        var failure = Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Contains("holds a mix", failure.Error.Reason, StringComparison.Ordinal);
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.Equal("old-b", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [Fact]
    public void Commit_WhenTheDestinationPathPassesThroughASymlink_RefusesToWriteThroughIt()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "irreplaceable");

        var staged = Path.Combine(_root, "staged-link");
        Directory.CreateDirectory(Path.Combine(staged, "data"));
        File.WriteAllText(Path.Combine(staged, "data", "secret.txt"), "new");

        var destination = Path.Combine(_root, "linked");
        Directory.CreateDirectory(destination);
        Directory.CreateSymbolicLink(Path.Combine(destination, "data"), outside);

        var result = Commit(_hostSystem, [new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory, false)]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("irreplaceable", File.ReadAllText(Path.Combine(outside, "secret.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private StagedExportArtifact Artifact(string name, string stagedContent, string currentContent, bool ownsDestination = true)
    {
        var staged = DirectoryWithFile($"staged-{name}", stagedContent);
        var destination = DirectoryWithFile(name, currentContent);
        return new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory, ownsDestination);
    }

    private static Result<Unit, ExportCommitError> Commit(IHostSystem hostSystem,
        IReadOnlyList<StagedExportArtifact> artifacts
    ) =>
        ExportCommitter.Commit(
            hostSystem,
            new DirectoryRemoval(hostSystem, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System),
            artifacts,
            NullLogger.Instance);

    private IHostSystem DelegatingHostThatFailsMove(string? directorySource = null,
        string? fileSource = null,
        string? deleteFailure = null,
        string? deleteDirectoryFailure = null
    )
    {
        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(system => system.FileExists(It.IsAny<string>()))
            .Returns((string path) => _hostSystem.FileExists(path));
        hostSystem.Setup(system => system.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => _hostSystem.DirectoryExists(path));
        hostSystem.Setup(system => system.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string source, string destination) =>
                string.Equals(source, directorySource, StringComparison.Ordinal)
                    ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.IoFailure(destination))
                    : _hostSystem.MoveDirectory(source, destination));
        hostSystem.Setup(system => system.CreateDirectory(It.IsAny<string>()))
            .Returns((string path) => _hostSystem.CreateDirectory(path));
        hostSystem.Setup(system => system.MoveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string source, string destination, bool overwrite) =>
                string.Equals(source, fileSource, StringComparison.Ordinal)
                    ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.IoFailure(destination))
                    : _hostSystem.MoveFile(source, destination, overwrite));
        hostSystem.Setup(system => system.DeleteFileIfExists(It.IsAny<string>()))
            .Returns((string path) => string.Equals(path, deleteFailure, StringComparison.Ordinal)
                ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.PermissionDenied(path))
                : _hostSystem.DeleteFileIfExists(path));
        hostSystem.Setup(system => system.DeleteDirectoryIfExists(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool recursive) => string.Equals(path, deleteDirectoryFailure, StringComparison.Ordinal)
                ? new Result<Unit, FileOperationError>.Failure(new FileOperationError.PermissionDenied(path))
                : _hostSystem.DeleteDirectoryIfExists(path, recursive));
        hostSystem.Setup(system => system.EnumerateEntries(It.IsAny<string>()))
            .Returns((string path) => _hostSystem.EnumerateEntries(path));
        return hostSystem.Object;
    }

    private string DirectoryWithFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "game.txt"), content);
        return path;
    }

    private string FileWithContent(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Read(string directory) => File.ReadAllText(Path.Combine(directory, "game.txt"));
}
