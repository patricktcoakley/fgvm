using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Tests.Godot.ReleaseManager;
using Fgvm.Types;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Fgvm.Tests.Godot;

public sealed class ProjectManagerTests : IDisposable
{
    private readonly ProjectManager _projectManager;
    private readonly string _tempDirectory;

    public ProjectManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"fgvm-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        var releaseManager = new ReleaseManagerBuilder().Build();
        var pathService = new Mock<IPathService>();
        var hostSystem = new HostSystem(new SystemInfo(), pathService.Object, NullLogger<HostSystem>.Instance);
        _projectManager = new ProjectManager(releaseManager, hostSystem);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void FindProjectVersion_WithFgvmVersionFile_ReturnsVersionFromFile()
    {
        const string versionContent = "4.3-stable";
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, versionContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Equal("4.3-stable-standard", result);
    }

    [Fact]
    public void FindProjectVersion_WithFgvmVersionFileContainingWhitespace_ReturnsTrimedVersion()
    {
        const string versionContent = "  4.3-stable  \n";
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, versionContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Equal("4.3-stable-standard", result);
    }

    [Fact]
    public void FindProjectVersion_WithEmptyFgvmVersionFile_FallsBackToProjectGodot()
    {
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, "   \n");

        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Equal("4.3-stable-standard", result);
    }

    [Fact]
    public void FindProjectVersion_WithProjectGodotFile_ReturnsVersionFromFeatures()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Equal("4.3-stable-standard", result);
    }

    [Fact]
    public void FindProjectVersion_WithProjectGodotFileContainingMultipleVersions_ReturnsFirstValidVersion()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "4.2", "Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Equal("4.3-stable-standard", result);
    }

    [Fact]
    public void FindProjectVersion_WithNoFiles_ReturnsNull()
    {
        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Null(result);
    }

    [Fact]
    public void FindProjectVersion_WithInvalidProjectGodotFile_ReturnsNull()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectVersionValue(_tempDirectory);

        Assert.Null(result);
    }

    [Fact]
    public void FindProjectInfo_WithFgvmVersionFile_ReturnsReleaseWithoutDotNet()
    {
        const string versionContent = "4.3-stable";
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, versionContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.3-stable-standard", result.ReleaseNameWithRuntime);
        Assert.False(result.IsDotNet);
    }

    [Fact]
    public void FindProjectInfo_WithFgvmVersionFileContainingMono_ReturnsReleaseWithDotNet()
    {
        const string versionContent = "4.3-stable-mono";
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, versionContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.3-stable-mono", result.ReleaseNameWithRuntime);
        Assert.True(result.IsDotNet);
    }

    [Fact]
    public void FindProjectInfo_WithProjectGodotDotNetProject_ReturnsProjectInfoWithDotNet()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "C#")

                                      [dotnet]
                                      project/assembly_name="TestProject"
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.3-stable-mono", result.ReleaseNameWithRuntime);
        Assert.True(result.IsDotNet);
    }

    [Fact]
    public void FindProjectInfo_WithProjectGodotStandardProject_ReturnsProjectInfoWithoutDotNet()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.3-stable-standard", result.ReleaseNameWithRuntime);
        Assert.False(result.IsDotNet);
    }

    [Theory]
    [InlineData("4.3", "4.3-stable-standard")]
    [InlineData("4.3.1", "4.3.1-stable-standard")]
    [InlineData("4.2", "4.2-stable-standard")]
    [InlineData("4.10.5", "4.10.5-stable-standard")]
    public void FindProjectInfo_WithValidVersionFormats_ReturnsCorrectVersion(string version, string expectedRelease)
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        var projectContent = $"""
                              [application]
                              config/name="Test Project"
                              config/features=PackedStringArray("{version}", "Forward Plus")
                              """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal(expectedRelease, result.ReleaseNameWithRuntime);
    }

    [Theory]
    [InlineData("config/features=PackedStringArray(\"4.3\", \"Forward Plus\")")]
    [InlineData("config/features=PackedStringArray( \"4.3\" , \"Forward Plus\" )")]
    [InlineData("config/features=PackedStringArray(\"Forward Plus\", \"4.3\")")]
    public void FindProjectInfo_WithDifferentFeatureFormatting_ParsesCorrectly(string featuresLine)
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        var projectContent = $"""
                              [application]
                              config/name="Test Project"
                              {featuresLine}
                              """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.3-stable-standard", result.ReleaseNameWithRuntime);
    }

    [Fact]
    public void FindProjectInfo_WithMalformedProjectGodotFile_ReturnsInvalidProjectFileFailure()
    {
        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("Forward Plus"
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = _projectManager.FindProjectInfo(_tempDirectory);

        var failure = Assert.IsType<Result<ProjectLookup<Release>, ProjectError>.Failure>(result);
        Assert.IsType<ProjectError.InvalidProjectFile>(failure.Error);
    }

    [Fact]
    public void CreateVersionFile_CreatesFileWithCorrectContent()
    {
        const string version = "4.3-stable";

        CreateVersionFile(version, _tempDirectory);

        var filePath = Path.Combine(_tempDirectory, ".fgvm-version");
        Assert.True(File.Exists(filePath));
        var content = File.ReadAllText(filePath);
        Assert.Equal($"4.3-stable{System.Environment.NewLine}", content);
    }

    [Fact]
    public void FindProjectInfo_PrioritizesFgvmVersionFileOverProjectGodot()
    {
        var versionFilePath = Path.Combine(_tempDirectory, ".fgvm-version");
        File.WriteAllText(versionFilePath, "4.4-dev5");

        var projectFilePath = Path.Combine(_tempDirectory, "project.godot");
        const string projectContent = """
                                      [application]
                                      config/name="Test Project"
                                      config/features=PackedStringArray("4.3", "Forward Plus")
                                      """;

        File.WriteAllText(projectFilePath, projectContent);

        var result = FindProjectInfoValue(_tempDirectory);

        Assert.NotNull(result);
        Assert.Equal("4.4-dev5-standard", result.ReleaseNameWithRuntime);
    }

    private Release? FindProjectInfoValue(string? directory = null) =>
        ProjectLookupToNullable(_projectManager.FindProjectInfo(directory));

    private string? FindProjectVersionValue(string? directory = null) =>
        ProjectLookupToNullable(_projectManager.FindProjectVersion(directory));

    private void CreateVersionFile(string version, string? directory = null) =>
        Assert.IsType<Result<Unit, ProjectError>.Success>(_projectManager.CreateVersionFile(version, directory));

    private static T? ProjectLookupToNullable<T>(Result<ProjectLookup<T>, ProjectError> result) where T : class =>
        result switch
        {
            Result<ProjectLookup<T>, ProjectError>.Success(ProjectLookup<T>.Found(var value)) => value,
            Result<ProjectLookup<T>, ProjectError>.Success(ProjectLookup<T>.Missing) => null,
            Result<ProjectLookup<T>, ProjectError>.Failure(var error) =>
                throw new InvalidOperationException($"Unexpected project error: {error}"),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
}
