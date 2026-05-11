using System.Text.RegularExpressions;
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

public partial class ProjectManager(IReleaseManager releaseManager) : IProjectManager
{
    private const string VersionFile = ".fgvm-version";
    private const string ProjectFile = "project.godot";

    /// <inheritdoc />
    public Result<ProjectLookup<Release>, ProjectError> FindProjectInfo(string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();

        try
        {
            // 1. Check for `.fgvm-version` file first (user override)
            var versionFile = Path.Combine(targetDir, VersionFile);
            if (File.Exists(versionFile))
            {
                var content = File.ReadAllText(versionFile).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    return CreateReleaseLookup(content);
                }
            }

            // 2. Check `project.godot` file for automatic detection
            var projectFile = Path.Combine(targetDir, ProjectFile);
            return File.Exists(projectFile)
                ? ParseProjectGodot(projectFile)
                : new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.PermissionDenied(targetDir));
        }
        catch (DirectoryNotFoundException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.DirectoryNotFound(targetDir));
        }
        catch (IOException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.ReadFailed(targetDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidPath(targetDir));
        }
    }

    /// <inheritdoc />
    public Result<ProjectLookup<string>, ProjectError> FindProjectFilePath(string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();
        try
        {
            var projectFile = Path.Combine(targetDir, ProjectFile);
            return File.Exists(projectFile)
                ? new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Found(projectFile))
                : new Result<ProjectLookup<string>, ProjectError>.Success(new ProjectLookup<string>.Missing());
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<ProjectLookup<string>, ProjectError>.Failure(new ProjectError.PermissionDenied(targetDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Result<ProjectLookup<string>, ProjectError>.Failure(new ProjectError.InvalidPath(targetDir));
        }
    }

    /// <inheritdoc />
    public Result<Unit, ProjectError> CreateVersionFile(string version, string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();
        var filePath = Path.Combine(targetDir, VersionFile);
        try
        {
            File.WriteAllText(filePath, version + System.Environment.NewLine);
            return new Result<Unit, ProjectError>.Success(Unit.Value);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<Unit, ProjectError>.Failure(new ProjectError.PermissionDenied(filePath));
        }
        catch (DirectoryNotFoundException)
        {
            return new Result<Unit, ProjectError>.Failure(new ProjectError.DirectoryNotFound(targetDir));
        }
        catch (IOException)
        {
            return new Result<Unit, ProjectError>.Failure(new ProjectError.WriteFailed(filePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Result<Unit, ProjectError>.Failure(new ProjectError.InvalidPath(filePath));
        }
    }

    /// <inheritdoc />
    public Result<ProjectLookup<Release>, ProjectError> FindExplicitProjectInfo(string? directory = null)
    {
        var targetDir = directory ?? Directory.GetCurrentDirectory();

        try
        {
            // Only check for `.fgvm-version` file (user override)
            var versionFile = Path.Combine(targetDir, VersionFile);
            if (!File.Exists(versionFile))
            {
                return new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());
            }

            var content = File.ReadAllText(versionFile).Trim();
            return string.IsNullOrEmpty(content)
                ? new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing())
                : CreateReleaseLookup(content);
        }
        catch (UnauthorizedAccessException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.PermissionDenied(targetDir));
        }
        catch (DirectoryNotFoundException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.DirectoryNotFound(targetDir));
        }
        catch (IOException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.ReadFailed(targetDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidPath(targetDir));
        }
    }

    /// <summary>
    ///     Finds the project version using the following priority:
    ///     1. `.fgvm-version` file (user override) or
    ///     2. `project.godot` file (automatic detection) and creates a `.fgvm-version` file based on the contents.
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

    /// <summary>
    ///     Parses a project.godot file to extract version and .NET information, then creates a Release.
    /// </summary>
    /// <param name="projectFilePath">Path to the project.godot file.</param>
    /// <returns>Project lookup result if parsing succeeds, or a project error.</returns>
    private Result<ProjectLookup<Release>, ProjectError> ParseProjectGodot(string projectFilePath)
    {
        var content = File.ReadAllText(projectFilePath);
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

            return CreateReleaseLookup(fullVersion);
        }

        if (foundFeaturesLine && malformedFeaturesLine)
        {
            return new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidProjectFile(projectFilePath));
        }

        return new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing());
    }

    private Result<ProjectLookup<Release>, ProjectError> CreateReleaseLookup(string version) =>
        releaseManager.CreateRelease(version) switch
        {
            Result<Release, ReleaseParseError>.Success(var release) =>
                new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Found(release)),
            Result<Release, ReleaseParseError>.Failure =>
                new Result<ProjectLookup<Release>, ProjectError>.Failure(new ProjectError.InvalidVersion(version)),
            _ => throw new InvalidOperationException("Unexpected Result type")
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
