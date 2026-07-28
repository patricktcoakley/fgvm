using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Services;

public sealed class RemovalServiceTests
{
    [Fact]
    public void Sweep_WhenExportTemplatePathIsInvalid_ContinuesWithFgvmOwnedRoots()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-removal-service-tests");
        var installationsPath = Path.Combine(rootPath, "installations");
        var releasePath = Path.Combine(installationsPath, "4.6.2-stable-standard");

        var directoryRemoval = new Mock<IDirectoryRemoval>();
        string[]? sweptDirectories = null;
        directoryRemoval.Setup(x => x.Sweep(It.IsAny<IEnumerable<string>>()))
            .Callback((IEnumerable<string> directories) => sweptDirectories = directories.ToArray());

        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(x => x.DirectoryExists(installationsPath))
            .Returns(new Result<bool, FileOperationError>.Success(true));
        hostSystem.Setup(x => x.EnumerateDirectories(installationsPath))
            .Returns(new Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(
                [new HostDirectoryEntry(releasePath, Path.GetFileName(releasePath), FileAttributes.Directory)]));

        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(rootPath);
        pathService.SetupGet(x => x.InstallationsDirectoryPath).Returns(installationsPath);

        var godotPathService = new Mock<IGodotPathService>();
        godotPathService.SetupGet(x => x.ExportTemplatesRootPath)
            .Throws(new InvalidOperationException("FGVM_GODOT_EXPORT_TEMPLATES_DIR must be an absolute path."));

        var service = new RemovalService(
            directoryRemoval.Object,
            hostSystem.Object,
            pathService.Object,
            godotPathService.Object,
            new TestConsole(),
            NullLogger<RemovalService>.Instance);

        var exception = Record.Exception(service.Sweep);

        Assert.Null(exception);
        Assert.Equal([rootPath, installationsPath, releasePath], Assert.IsType<string[]>(sweptDirectories));
    }
}
