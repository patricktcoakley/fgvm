using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Environment;

/// <summary>
///     Pins the filesystem semantics callers rely on, so replacing a direct System.IO call with the equivalent
///     <see cref="IHostSystem" /> call is provably behaviour-preserving.
/// </summary>
public sealed class HostSystemContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-host-system-contract-tests",
        Guid.NewGuid().ToString("N"));

    private readonly IHostSystem _hostSystem;

    public HostSystemContractTests()
    {
        Directory.CreateDirectory(_root);
        var pathService = new Mock<IPathService>();
        pathService.SetupGet(x => x.RootPath).Returns(_root);
        _hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MoveDirectory_RenamesTheDirectoryAndItsContents()
    {
        var source = CreateDirectory("source", "file.txt", "contents");
        var destination = Path.Combine(_root, "destination");

        AssertSuccess(_hostSystem.MoveDirectory(source, destination));

        Assert.False(Directory.Exists(source));
        Assert.Equal("contents", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void MoveDirectory_ReturnsNotFound_WhenTheSourceIsMissing()
    {
        var source = Path.Combine(_root, "not-there");
        var destination = Path.Combine(_root, "destination");

        var error = AssertFailure(_hostSystem.MoveDirectory(source, destination));

        Assert.IsType<FileOperationError.NotFound>(error);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void MoveDirectory_Fails_WhenTheDestinationAlreadyExists()
    {
        var source = CreateDirectory("source", "file.txt", "new");
        var destination = CreateDirectory("destination", "file.txt", "old");

        // Callers depend on this: a rename never silently replaces an existing directory, which is why replacing one
        // takes a move-aside-then-move-in dance.
        AssertFailure(_hostSystem.MoveDirectory(source, destination));

        Assert.Equal("new", File.ReadAllText(Path.Combine(source, "file.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void DeleteDirectoryIfExists_ReturnsSuccess_WhenTheDirectoryIsMissing()
    {
        AssertSuccess(_hostSystem.DeleteDirectoryIfExists(Path.Combine(_root, "not-there"), true));
    }

    [Fact]
    public void DeleteFileIfExists_ReturnsSuccess_WhenTheParentDirectoryIsMissing()
    {
        AssertSuccess(_hostSystem.DeleteFileIfExists(Path.Combine(_root, "not-there", "file.txt")));
    }

    [Fact]
    public void DeleteDirectoryIfExists_RemovesAPopulatedTree_WhenRecursive()
    {
        var directory = CreateDirectory("populated", "file.txt", "contents");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "deep.txt"), "deep");

        AssertSuccess(_hostSystem.DeleteDirectoryIfExists(directory, true));

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DeleteDirectoryIfExists_ReportsFailureWithoutThrowing_WhenTheParentIsNotWritable()
    {
        if (OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess)
        {
            return;
        }

        var parent = CreateDirectory("parent", "file.txt", "contents");
        var child = CreateDirectory(Path.Combine("parent", "child"), "file.txt", "contents");
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            // Which variant comes back depends on what the OS raises: an unwritable parent surfaces as IoFailure here,
            // not PermissionDenied. Callers only rely on getting a failure rather than an exception.
            AssertFailure(_hostSystem.DeleteDirectoryIfExists(child, true));

            Assert.True(Directory.Exists(child));
        }
        finally
        {
            File.SetUnixFileMode(parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void DirectoryExists_DistinguishesDirectoriesFromFilesAndMissingPaths()
    {
        var directory = CreateDirectory("directory", "file.txt", "contents");

        Assert.True(AssertSuccess(_hostSystem.DirectoryExists(directory)));
        Assert.False(AssertSuccess(_hostSystem.DirectoryExists(Path.Combine(_root, "not-there"))));
        Assert.False(AssertSuccess(_hostSystem.DirectoryExists(Path.Combine(directory, "file.txt"))));
    }

    [Fact]
    public void CreateDirectory_IsIdempotent()
    {
        var directory = Path.Combine(_root, "created");

        AssertSuccess(_hostSystem.CreateDirectory(directory));
        AssertSuccess(_hostSystem.CreateDirectory(directory));

        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void EnumerateDirectories_ReturnsChildDirectoriesOnly()
    {
        CreateDirectory("child", "file.txt", "contents");
        File.WriteAllText(Path.Combine(_root, "loose.txt"), "loose");

        var entries = AssertSuccess(_hostSystem.EnumerateDirectories(_root));

        Assert.Single(entries);
        Assert.Equal("child", entries[0].Name);
    }

    [Fact]
    public void EnumerateDirectories_ReturnsNotFound_WhenThePathIsMissing()
    {
        var error = AssertFailure(_hostSystem.EnumerateDirectories(Path.Combine(_root, "not-there")));

        Assert.IsType<FileOperationError.NotFound>(error);
    }

    [Fact]
    public void FileExists_DistinguishesFilesFromDirectoriesAndMissingPaths()
    {
        var directory = CreateDirectory("directory", "file.txt", "contents");

        Assert.True(AssertSuccess(_hostSystem.FileExists(Path.Combine(directory, "file.txt"))));
        Assert.False(AssertSuccess(_hostSystem.FileExists(Path.Combine(_root, "not-there.txt"))));
        Assert.False(AssertSuccess(_hostSystem.FileExists(directory)));
    }

    private static T AssertSuccess<T>(Result<T, FileOperationError> result) =>
        Assert.IsType<Result<T, FileOperationError>.Success>(result).Value;

    private static FileOperationError AssertFailure<T>(Result<T, FileOperationError> result) =>
        Assert.IsType<Result<T, FileOperationError>.Failure>(result).Error;

    private string CreateDirectory(string relativePath, string fileName, string content)
    {
        var directory = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        return directory;
    }
}
