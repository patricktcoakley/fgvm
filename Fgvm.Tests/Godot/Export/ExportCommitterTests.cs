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
        var first = new StagedExportArtifact(stagedFile, destinationFile, ExportArtifactKind.File);
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
        var artifact = new StagedExportArtifact(stagedFile, destinationFile, ExportArtifactKind.File);
        var hostSystem = DelegatingHostThatFailsMove(fileSource: destinationFile);

        var result = Commit(hostSystem, [artifact]);

        Assert.IsType<Result<Unit, ExportCommitError>.Failure>(result);
        Assert.Equal("old-file", File.ReadAllText(destinationFile));
        Assert.Empty(Directory.GetDirectories(_root, ".backup-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private StagedExportArtifact Artifact(string name, string stagedContent, string currentContent)
    {
        var staged = DirectoryWithFile($"staged-{name}", stagedContent);
        var destination = DirectoryWithFile(name, currentContent);
        return new StagedExportArtifact(staged, destination, ExportArtifactKind.Directory);
    }

    private static Result<Unit, ExportCommitError> Commit(IHostSystem hostSystem,
        IReadOnlyList<StagedExportArtifact> artifacts
    ) =>
        ExportCommitter.Commit(
            hostSystem,
            new DirectoryRemoval(hostSystem, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System),
            artifacts,
            NullLogger.Instance);

    private IHostSystem DelegatingHostThatFailsMove(string? directorySource = null, string? fileSource = null)
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
            .Returns((string path) => _hostSystem.DeleteFileIfExists(path));
        hostSystem.Setup(system => system.DeleteDirectoryIfExists(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool recursive) => _hostSystem.DeleteDirectoryIfExists(path, recursive));
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
