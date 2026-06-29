using System.Text.RegularExpressions;
using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Godot;

/// <summary>
///     Interface for managing Godot project information and version files
/// </summary>
public interface IProjectManager
{
    /// <summary>
    ///     Finds project release information including version and runtime environment.
    /// </summary>
    /// <param name="directory">The directory to search in. If null, uses current working directory.</param>
    /// <returns>Project lookup result if successful, or a project error.</returns>
    Result<ProjectLookup<Release>, ProjectError> FindProjectInfo(string? directory = null);

    /// <summary>
    ///     Finds project release information without binding it to the current host platform.
    /// </summary>
    /// <param name="directory">The directory to search in. If null, uses current working directory.</param>
    /// <returns>Project lookup result if successful, or a project error.</returns>
    Result<ProjectLookup<Release>, ProjectError> FindProjectInfoWithoutPlatform(string? directory = null);


    /// <summary>
    ///     Finds the path to the project.godot file in the specified directory.
    /// </summary>
    /// <param name="directory">The directory to search in. If null, uses current working directory.</param>
    /// <returns>Project file lookup result if successful, or a project error.</returns>
    Result<ProjectLookup<string>, ProjectError> FindProjectFilePath(string? directory = null);

    /// <summary>
    ///     Creates or updates a `.fgvm-version` file in the specified directory.
    /// </summary>
    /// <param name="version">The version to write to the file</param>
    /// <param name="directory">The directory to create the file in (null for current directory)</param>
    Result<Unit, ProjectError> CreateVersionFile(string version, string? directory = null);

    /// <summary>
    ///     Finds project release info from `.fgvm-version` file only
    /// </summary>
    /// <param name="directory">The directory to search in. If null, uses current working directory.</param>
    /// <returns>Explicit project lookup result if successful, or a project error.</returns>
    Result<ProjectLookup<Release>, ProjectError> FindExplicitProjectInfo(string? directory = null);
}

public partial class ProjectManager(IReleaseManager releaseManager, IHostSystem hostSystem) : IProjectManager
{
    private const string VersionFile = ".fgvm-version";
    private const string ProjectFile = "project.godot";

    /// <inheritdoc />
    public Result<ProjectLookup<Release>, ProjectError> FindProjectInfo(string? directory = null) =>
        FindProjectInfoCore(releaseManager.CreateRelease, directory);

    /// <inheritdoc />
    public Result<ProjectLookup<Release>, ProjectError> FindProjectInfoWithoutPlatform(string? directory = null) =>
        FindProjectInfoCore(releaseManager.CreateReleaseWithoutPlatform, directory);

    private Result<ProjectLookup<Release>, ProjectError> FindProjectInfoCore(Func<string, Result<Release, ReleaseParseError>> createRelease,
        string? directory = null
    )
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();

        // 1. Check for `.fgvm-version` file first (user override)
        var versionFile = Path.Combine(targetDir, VersionFile);
        switch (hostSystem.FileExists(versionFile))
        {
            case Result<bool, FileOperationError>.Failure(var versionExistsError):
                return new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(versionExistsError, targetDir));
            case Result<bool, FileOperationError>.Success { Value: true }:
                switch (hostSystem.ReadAllText(versionFile))
                {
                    case Result<string, FileOperationError>.Failure(var readError):
                        return new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(readError, targetDir));
                    case Result<string, FileOperationError>.Success(var contentValue):
                        var content = contentValue.Trim();
                        if (!string.IsNullOrEmpty(content))
                        {
                            return CreateReleaseLookup(content, createRelease);
                        }

                        break;
                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }

                break;
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        // 2. Check `project.godot` file for automatic detection
        var projectFile = Path.Combine(targetDir, ProjectFile);
        return hostSystem.FileExists(projectFile) switch
        {
            Result<bool, FileOperationError>.Failure(var projectExistsError) =>
                new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(projectExistsError, targetDir)),
            Result<bool, FileOperationError>.Success { Value: true } => ParseProjectGodot(projectFile, createRelease),
            Result<bool, FileOperationError>.Success =>
                new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing()),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<ProjectLookup<string>, ProjectError> FindProjectFilePath(string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();
        var projectFile = Path.Combine(targetDir, ProjectFile);
        return hostSystem.FileExists(projectFile) switch
        {
            Result<bool, FileOperationError>.Failure(var projectExistsError) =>
                new Result<ProjectLookup<string>, ProjectError>.Failure(ToProjectReadError(projectExistsError, targetDir)),
            Result<bool, FileOperationError>.Success { Value: true } =>
                new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Found(projectFile)),
            Result<bool, FileOperationError>.Success =>
                new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Missing()),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<Unit, ProjectError> CreateVersionFile(string version, string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();
        var filePath = Path.Combine(targetDir, VersionFile);

        return hostSystem.WriteAllText(filePath, version + System.Environment.NewLine) switch
        {
            Result<Unit, FileOperationError>.Success =>
                new Result<Unit, ProjectError>.Success(Unit.Value),
            Result<Unit, FileOperationError>.Failure(var fileError) =>
                new Result<Unit, ProjectError>.Failure(ToProjectWriteError(fileError, targetDir)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<ProjectLookup<Release>, ProjectError> FindExplicitProjectInfo(string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();

        // Only check for `.fgvm-version` file (user override)
        var versionFile = Path.Combine(targetDir, VersionFile);
        switch (hostSystem.FileExists(versionFile))
        {
            case Result<bool, FileOperationError>.Failure(var existsError):
                return new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(existsError, targetDir));
            case Result<bool, FileOperationError>.Success { Value: false }:
                return new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        switch (hostSystem.ReadAllText(versionFile))
        {
            case Result<string, FileOperationError>.Failure(var readError):
                return new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(readError, targetDir));
            case Result<string, FileOperationError>.Success(var contentValue):
                var content = contentValue.Trim();
                return string.IsNullOrEmpty(content)
                    ? new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing())
                    : CreateReleaseLookup(content, releaseManager.CreateRelease);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <summary>
    ///     Finds the project version using the following priority:
    ///     1. `.fgvm-version` file (user override) or
    ///     2. `project.godot` file (automatic detection).
    /// </summary>
    /// <param name="directory">The directory to search in. If null, uses current working directory.</param>
    /// <returns>The version string if found, null otherwise.</returns>
    public Result<ProjectLookup<string>, ProjectError> FindProjectVersion(string? directory = null)
    {
        return FindProjectInfo(directory) switch
        {
            Result<ProjectLookup<Release>, ProjectError>.Success(ProjectLookup<Release>.Found(var release)) =>
                new Result<ProjectLookup<string>, ProjectError>.Success(
                    new ProjectLookup<string>.Found(release.ReleaseNameWithRuntime)),
            Result<ProjectLookup<Release>, ProjectError>.Success =>
                new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Missing()),
            Result<ProjectLookup<Release>, ProjectError>.Failure(var error) =>
                new Result<ProjectLookup<string>, ProjectError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private Result<ProjectLookup<Release>, ProjectError> ParseProjectGodot(string projectFilePath,
        Func<string, Result<Release, ReleaseParseError>> createRelease
    )
    {
        string content;
        switch (hostSystem.ReadAllText(projectFilePath))
        {
            case Result<string, FileOperationError>.Failure(var readError):
                var targetDir = Path.GetDirectoryName(projectFilePath) ?? projectFilePath;
                return new Result<ProjectLookup<Release>, ProjectError>.Failure(ToProjectReadError(readError, targetDir));
            case Result<string, FileOperationError>.Success(var contentValue):
                content = contentValue;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string? version = null;
        var runtime = RuntimeEnvironment.Standard;
        var foundFeaturesLine = false;
        var malformedFeaturesLine = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Extract version from config/features
            if (trimmedLine.StartsWith("config/features=PackedStringArray("))
            {
                foundFeaturesLine = true;
                malformedFeaturesLine = trimmedLine.LastIndexOf(')') <= trimmedLine.IndexOf('(');
                version = ExtractVersionFromFeatures(trimmedLine);
            }

            // Check for .NET section
            if (trimmedLine == "[dotnet]")
            {
                runtime = RuntimeEnvironment.Mono;
            }
        }

        if (version != null)
        {
            // For project.godot versions (like "4.3"), we need to append "-stable" and runtime
            // to create a valid release name
            var versionWithType = version.Contains('-') ? version : $"{version}-stable";
            var runtimeSuffix = runtime == RuntimeEnvironment.Mono ? "-mono" : "-standard";
            var fullVersion = $"{versionWithType}{runtimeSuffix}";

            return CreateReleaseLookup(fullVersion, createRelease);
        }

        if (foundFeaturesLine && malformedFeaturesLine)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidProjectFile(projectFilePath));
        }

        return new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());
    }

    private Result<ProjectLookup<Release>, ProjectError> CreateReleaseLookup(string version,
        Func<string, Result<Release, ReleaseParseError>> createRelease
    ) =>
        createRelease(version) switch
        {
            Result<Release, ReleaseParseError>.Success(var release) =>
                new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Found(release)),
            Result<Release, ReleaseParseError>.Failure =>
                new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidVersion(version)),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private static ProjectError ToProjectReadError(FileOperationError error, string targetDir) => error switch
    {
        FileOperationError.PermissionDenied permissionDenied => new ProjectError.PermissionDenied(permissionDenied.Path),
        FileOperationError.NotFound => new ProjectError.DirectoryNotFound(targetDir),
        FileOperationError.InvalidPath invalidPath => new ProjectError.InvalidPath(invalidPath.Path),
        FileOperationError.UnsupportedPath unsupportedPath => new ProjectError.InvalidPath(unsupportedPath.Path),
        FileOperationError.IoFailure ioFailure => new ProjectError.ReadFailed(ioFailure.Path),
        _ => new ProjectError.ReadFailed(error.Path)
    };

    private static ProjectError ToProjectWriteError(FileOperationError error, string targetDir) => error switch
    {
        FileOperationError.PermissionDenied permissionDenied => new ProjectError.PermissionDenied(permissionDenied.Path),
        FileOperationError.NotFound => new ProjectError.DirectoryNotFound(targetDir),
        FileOperationError.InvalidPath invalidPath => new ProjectError.InvalidPath(invalidPath.Path),
        FileOperationError.UnsupportedPath unsupportedPath => new ProjectError.InvalidPath(unsupportedPath.Path),
        FileOperationError.IoFailure ioFailure => new ProjectError.WriteFailed(ioFailure.Path),
        _ => new ProjectError.WriteFailed(error.Path)
    };

    /// <summary>
    ///     Extracts the Godot version from a config/features line.
    /// </summary>
    /// <param name="featuresLine">The line containing config/features=PackedStringArray(...)</param>
    /// <returns>The version string if found, null otherwise.</returns>
    private static string? ExtractVersionFromFeatures(string featuresLine)
    {
        // Example: config/features=PackedStringArray("4.4", "Forward Plus")
        var startIndex = featuresLine.IndexOf('(');
        var endIndex = featuresLine.LastIndexOf(')');

        if (startIndex == -1 || endIndex == -1 || endIndex <= startIndex)
        {
            return null;
        }

        var featuresContent = featuresLine.Substring(startIndex + 1, endIndex - startIndex - 1);

        // Split by comma and look for version-like strings
        var features = featuresContent.Split(',')
            .Select(f => f.Trim().Trim('"'))
            .Where(f => !string.IsNullOrEmpty(f));

        return features.FirstOrDefault(feature => VersionRegex().IsMatch(feature));
    }

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial Regex VersionRegex();
}
