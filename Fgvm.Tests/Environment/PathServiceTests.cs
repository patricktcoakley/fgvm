using Fgvm.Environment;

namespace Fgvm.Tests.Environment;

[CollectionDefinition(nameof(EnvironmentVariableCollection), DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;

[Collection(nameof(EnvironmentVariableCollection))]
public sealed class PathServiceTests : IDisposable
{
    private readonly string? _originalFgvmHome = System.Environment.GetEnvironmentVariable("FGVM_HOME");

    [Fact]
    public void RootPath_UsesDefaultFgvmDirectory_WhenFgvmHomeIsUnset()
    {
        System.Environment.SetEnvironmentVariable("FGVM_HOME", null);
        var pathService = new PathService();

        var expected = Path.GetFullPath("fgvm",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

        Assert.Equal(expected, pathService.RootPath);
    }

    [Fact]
    public void RootPath_UsesFgvmHomeAsExactRoot_WhenFgvmHomeIsSet()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "fgvm-root");
        System.Environment.SetEnvironmentVariable("FGVM_HOME", rootPath);
        var pathService = new PathService();

        Assert.Equal(Path.GetFullPath(rootPath), pathService.RootPath);
        Assert.NotEqual(Path.Combine(Path.GetFullPath(rootPath), "fgvm"), pathService.RootPath);
    }

    [Fact]
    public void RootPath_UsesDefaultFgvmDirectory_WhenFgvmHomeIsEmpty()
    {
        System.Environment.SetEnvironmentVariable("FGVM_HOME", "");
        var pathService = new PathService();

        var expected = Path.GetFullPath("fgvm",
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

        Assert.Equal(expected, pathService.RootPath);
    }

    [Fact]
    public void RootPath_RejectsRelativeFgvmHome()
    {
        System.Environment.SetEnvironmentVariable("FGVM_HOME", "relative-fgvm-root");
        var pathService = new PathService();

        var exception = Assert.Throws<InvalidOperationException>(() => pathService.RootPath);
        Assert.Equal("FGVM_HOME must be an absolute path.", exception.Message);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable("FGVM_HOME", _originalFgvmHome);
    }
}
