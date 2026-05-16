using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fgvm.Services;

/// <summary>
///     Tracks installed Godot releases, their install paths, and default selection.
/// </summary>
public interface IInstallationRegistry
{
    /// <summary>
    ///     Lists validated installations from the registry.
    /// </summary>
    /// <returns>Installed Godot records, or a registry error.</returns>
    Result<IReadOnlyList<Installation>, InstallationRegistryError> ListInstallations();

    /// <summary>
    ///     Finds an installation for the current host target by release name.
    /// </summary>
    /// <param name="releaseNameWithRuntime">The release name with runtime suffix.</param>
    /// <returns>The installation record, or a registry error.</returns>
    Result<Installation, InstallationRegistryError> FindByReleaseName(string releaseNameWithRuntime);

    /// <summary>
    ///     Gets the current default installation.
    /// </summary>
    /// <returns>The default installation record, or a registry error.</returns>
    Result<Installation, InstallationRegistryError> GetDefault();

    /// <summary>
    ///     Adds or updates an installation record.
    /// </summary>
    /// <param name="release">The installed release.</param>
    /// <param name="relativePath">The install path relative to the fgvm root.</param>
    /// <param name="installedAt">The installation time. Defaults to now.</param>
    /// <returns>Success, or a registry error.</returns>
    Result<Unit, InstallationRegistryError> UpsertInstalled(Release release, string relativePath, DateTimeOffset? installedAt = null);

    /// <summary>
    ///     Sets the default installation by key.
    /// </summary>
    /// <param name="key">The installation key.</param>
    /// <returns>Success, or a registry error.</returns>
    Result<Unit, InstallationRegistryError> SetDefault(string key);

    /// <summary>
    ///     Clears the default installation.
    /// </summary>
    /// <returns>Success, or a registry error.</returns>
    Result<Unit, InstallationRegistryError> ClearDefault();

    /// <summary>
    ///     Removes an installation from the registry.
    /// </summary>
    /// <param name="key">The installation key.</param>
    /// <returns>Success, or a registry error.</returns>
    Result<Unit, InstallationRegistryError> Remove(string key);

    /// <summary>
    ///     Records a successful launch for an installation.
    /// </summary>
    /// <param name="key">The installation key.</param>
    /// <param name="launchedAt">The launch time. Defaults to now.</param>
    /// <returns>Success, or a registry error.</returns>
    Result<Unit, InstallationRegistryError> RecordLaunch(string key, DateTimeOffset? launchedAt = null);
}

public sealed class InstallationRegistry(
    IPathService pathService,
    IReleaseManager releaseManager,
    IHostSystem hostSystem,
    ILogger<InstallationRegistry> logger) : IInstallationRegistry
{
    private const string InstallationsDirectoryName = "installations";

    /// <inheritdoc />
    public Result<IReadOnlyList<Installation>, InstallationRegistryError> ListInstallations()
    {
        var documentResult = LoadDocument();
        return documentResult switch
        {
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document) =>
                new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(
                    document.Installations
                        .Select(kvp => ToInstalledGodot(kvp.Key, kvp.Value))
                        .OrderByDescending(installation => Release.TryParse(installation.ReleaseNameWithRuntime))
                        .ThenBy(installation => installation.Target, StringComparer.Ordinal)
                        .ToArray()),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error) =>
                new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<Installation, InstallationRegistryError> FindByReleaseName(string releaseNameWithRuntime)
    {
        if (releaseManager.CreateRelease(releaseNameWithRuntime) is not Result<Release, ReleaseParseError>.Success(var release))
        {
            return new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound(releaseNameWithRuntime));
        }

        var key = CreateKey(release.ReleaseNameWithRuntime, release.PlatformString);
        return LoadDocument() switch
        {
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document)
                when document.Installations.TryGetValue(key, out var entry) =>
                new Result<Installation, InstallationRegistryError>.Success(ToInstalledGodot(key, entry)),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document) =>
                FindGeneratedInstallation(key, document),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error) =>
                new Result<Installation, InstallationRegistryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    /// <inheritdoc />
    public Result<Installation, InstallationRegistryError> GetDefault() =>
        LoadDocument() switch
        {
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document)
                when document.Default is { } key && document.Installations.TryGetValue(key, out var entry) =>
                new Result<Installation, InstallationRegistryError>.Success(ToInstalledGodot(key, entry)),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success =>
                new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("default")),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error) =>
                new Result<Installation, InstallationRegistryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    /// <inheritdoc />
    public Result<Unit, InstallationRegistryError> UpsertInstalled(Release release, string relativePath, DateTimeOffset? installedAt = null)
    {
        var key = CreateKey(release.ReleaseNameWithRuntime, release.PlatformString);
        switch (LoadDocument())
        {
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error):
                return new Result<Unit, InstallationRegistryError>.Failure(error);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document):
                if (!IsSafeRelativePath(relativePath))
                {
                    return new Result<Unit, InstallationRegistryError>.Failure(new InstallationRegistryError.InvalidPath(relativePath));
                }

                var previous = document.Installations.GetValueOrDefault(key);
                document.Installations[key] = new InstallationRegistryEntry
                {
                    Path = NormalizeRelativePath(relativePath),
                    InstalledAt = previous?.InstalledAt ?? installedAt ?? DateTimeOffset.UtcNow,
                    LastLaunchedAt = previous?.LastLaunchedAt
                };

                return WriteDocument(document);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public Result<Unit, InstallationRegistryError> SetDefault(string key)
    {
        switch (LoadDocument())
        {
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error):
                return new Result<Unit, InstallationRegistryError>.Failure(error);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document)
                when !document.Installations.ContainsKey(key):
                return new Result<Unit, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound(key));
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document):
                document.Default = key;
                return WriteDocument(document);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public Result<Unit, InstallationRegistryError> ClearDefault()
    {
        switch (LoadDocument())
        {
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error):
                return new Result<Unit, InstallationRegistryError>.Failure(error);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document):
                document.Default = null;
                return WriteDocument(document);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public Result<Unit, InstallationRegistryError> Remove(string key)
    {
        switch (LoadDocument())
        {
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error):
                return new Result<Unit, InstallationRegistryError>.Failure(error);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document):
                document.Installations.Remove(key);
                if (string.Equals(document.Default, key, StringComparison.Ordinal))
                {
                    document.Default = null;
                }

                return WriteDocument(document);
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public Result<Unit, InstallationRegistryError> RecordLaunch(string key, DateTimeOffset? launchedAt = null)
    {
        switch (LoadDocument())
        {
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error):
                return new Result<Unit, InstallationRegistryError>.Failure(error);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var document)
                when document.Installations.TryGetValue(key, out var entry):
                entry.LastLaunchedAt = launchedAt ?? DateTimeOffset.UtcNow;
                return WriteDocument(document);
            case Result<InstallationRegistryDocument, InstallationRegistryError>.Success:
                return new Result<Unit, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound(key));
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private Result<Installation, InstallationRegistryError> FindGeneratedInstallation(string key, InstallationRegistryDocument document) =>
        GenerateFromFileSystem(document) switch
        {
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success(var generated)
                when generated.Installations.TryGetValue(key, out var entry) =>
                new Result<Installation, InstallationRegistryError>.Success(ToInstalledGodot(key, entry)),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Success =>
                new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound(key)),
            Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(var error) =>
                new Result<Installation, InstallationRegistryError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };

    private Result<InstallationRegistryDocument, InstallationRegistryError> LoadDocument()
    {
        switch (hostSystem.FileExists(pathService.InstallationsPath))
        {
            case Result<bool, FileOperationError>.Failure(var existsError):
                return new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(
                    new InstallationRegistryError.ReadFailed(existsError));
            case Result<bool, FileOperationError>.Success { Value: false }:
                return GenerateFromFileSystem(null);
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        string json;
        switch (hostSystem.ReadAllText(pathService.InstallationsPath))
        {
            case Result<string, FileOperationError>.Failure(var readError):
                return new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(
                    new InstallationRegistryError.ReadFailed(readError));
            case Result<string, FileOperationError>.Success(var content):
                json = content;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        try
        {
            if (JsonSerializer.Deserialize(json,
                    InstallationRegistryJsonContext.Default.InstallationRegistryDocument) is not { } document)
            {
                return GenerateFromFileSystem(null);
            }

            RegistryValidation registryValidation;
            switch (ValidateDocument(document))
            {
                case Result<RegistryValidation, FileOperationError>.Failure(var validationError):
                    return new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(
                        new InstallationRegistryError.ReadFailed(validationError));
                case Result<RegistryValidation, FileOperationError>.Success(var validation):
                    registryValidation = validation;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            if (registryValidation.NeedsFileSystemGeneration)
            {
                return GenerateFromFileSystem(document);
            }

            if (!registryValidation.Changed)
            {
                return new Result<InstallationRegistryDocument, InstallationRegistryError>.Success(registryValidation.Document);
            }

            return WriteDocument(registryValidation.Document) switch
            {
                Result<Unit, InstallationRegistryError>.Failure(var writeError) =>
                    new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(writeError),
                Result<Unit, InstallationRegistryError>.Success =>
                    new Result<InstallationRegistryDocument, InstallationRegistryError>.Success(registryValidation.Document),
                _ => throw new InvalidOperationException("Unexpected Result type")
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Installation registry is invalid; generating from filesystem");
            return GenerateFromFileSystem(null);
        }
    }

    private Result<RegistryValidation, FileOperationError> ValidateDocument(InstallationRegistryDocument document)
    {
        var normalized = new InstallationRegistryDocument { Default = document.Default };
        var changed = false;
        var needsGeneration = false;

        foreach (var (key, entry) in document.Installations)
        {
            if (!TryParseKey(key, out var releaseName, out var target) ||
                string.IsNullOrWhiteSpace(entry.Path) ||
                !IsSafeRelativePath(entry.Path))
            {
                changed = true;
                continue;
            }

            var fullPath = ResolvePath(entry.Path);
            switch (hostSystem.DirectoryExists(fullPath))
            {
                case Result<bool, FileOperationError>.Failure(var directoryError):
                    return new Result<RegistryValidation, FileOperationError>.Failure(directoryError);
                case Result<bool, FileOperationError>.Success { Value: false }:
                    needsGeneration = true;
                    break;
                case Result<bool, FileOperationError>.Success:
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            if (needsGeneration)
            {
                break;
            }

            var normalizedPath = NormalizeRelativePath(entry.Path);
            normalized.Installations[CreateKey(releaseName, target)] = new InstallationRegistryEntry
            {
                Path = normalizedPath,
                InstalledAt = entry.InstalledAt,
                LastLaunchedAt = entry.LastLaunchedAt
            };

            changed |= !string.Equals(key, CreateKey(releaseName, target), StringComparison.Ordinal) ||
                       !string.Equals(entry.Path, normalizedPath, StringComparison.Ordinal);
        }

        if (normalized.Default is not null && !normalized.Installations.ContainsKey(normalized.Default))
        {
            normalized.Default = null;
            changed = true;
        }

        return new Result<RegistryValidation, FileOperationError>.Success(
            new RegistryValidation(normalized, changed, needsGeneration));
    }

    private Result<InstallationRegistryDocument, InstallationRegistryError> GenerateFromFileSystem(
        InstallationRegistryDocument? existingDocument)
    {
        InstallationRegistryDocument generated;
        switch (ScanInstallations(existingDocument))
        {
            case Result<InstallationRegistryDocument, FileOperationError>.Failure(var scanError):
                return new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(
                    new InstallationRegistryError.GenerationFailed(scanError));
            case Result<InstallationRegistryDocument, FileOperationError>.Success(var document):
                generated = document;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        if (existingDocument?.Default is { } existingDefault && generated.Installations.ContainsKey(existingDefault))
        {
            generated.Default = existingDefault;
        }
        else
        {
            generated.Default = InferDefaultFromSymlink(generated);
        }

        switch (WriteDocument(generated))
        {
            case Result<Unit, InstallationRegistryError>.Failure(var writeError):
                return new Result<InstallationRegistryDocument, InstallationRegistryError>.Failure(writeError);
            case Result<Unit, InstallationRegistryError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        RefreshDefaultArtifacts(generated);
        return new Result<InstallationRegistryDocument, InstallationRegistryError>.Success(generated);
    }

    private Result<InstallationRegistryDocument, FileOperationError> ScanInstallations(InstallationRegistryDocument? existingDocument)
    {
        var document = new InstallationRegistryDocument();

        // LEGACY COMPATIBILITY: Remove this block with ScanLegacyInstallations when legacy <root>/<release>
        // installs are no longer supported.
        IReadOnlyList<Installation> legacy;
        switch (ScanLegacyInstallations(existingDocument))
        {
            case Result<IReadOnlyList<Installation>, FileOperationError>.Failure(var legacyError):
                return new Result<InstallationRegistryDocument, FileOperationError>.Failure(legacyError);
            case Result<IReadOnlyList<Installation>, FileOperationError>.Success(var installations):
                legacy = installations;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        foreach (var installation in legacy)
        {
            document.Installations[installation.Key] = ToEntry(installation);
        }

        IReadOnlyList<Installation> targetAware;
        switch (ScanTargetAwareInstallations(existingDocument))
        {
            case Result<IReadOnlyList<Installation>, FileOperationError>.Failure(var targetAwareError):
                return new Result<InstallationRegistryDocument, FileOperationError>.Failure(targetAwareError);
            case Result<IReadOnlyList<Installation>, FileOperationError>.Success(var installations):
                targetAware = installations;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        foreach (var installation in targetAware)
        {
            document.Installations[installation.Key] = ToEntry(installation);
        }

        return new Result<InstallationRegistryDocument, FileOperationError>.Success(document);
    }

    private Result<IReadOnlyList<Installation>, FileOperationError> ScanLegacyInstallations(InstallationRegistryDocument? existingDocument)
    {
        // LEGACY COMPATIBILITY: This imports pre-registry installs from <root>/<release>.
        // Keep new installs target-aware; delete this method when dropping legacy layout support.
        switch (hostSystem.DirectoryExists(pathService.RootPath))
        {
            case Result<bool, FileOperationError>.Failure(var rootError):
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Failure(rootError);
            case Result<bool, FileOperationError>.Success { Value: false }:
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Success([]);
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        IReadOnlyList<HostDirectoryEntry> directories;
        switch (hostSystem.EnumerateDirectories(pathService.RootPath))
        {
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var directoriesError):
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Failure(directoriesError);
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                directories = entries;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var installations = new List<Installation>();
        foreach (var info in directories)
        {
            if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                info.Name.StartsWith(".", StringComparison.Ordinal) ||
                string.Equals(info.Name, "bin", StringComparison.Ordinal) ||
                string.Equals(info.Name, InstallationsDirectoryName, StringComparison.Ordinal) ||
                string.Equals(info.FullName, pathService.SymlinkPath, StringComparison.Ordinal) ||
                string.Equals(info.FullName, pathService.MacAppSymlinkPath, StringComparison.Ordinal))
            {
                continue;
            }

            if (releaseManager.CreateRelease(info.Name) is not Result<Release, ReleaseParseError>.Success(var release) ||
                release.PlatformString is null)
            {
                continue;
            }

            var key = CreateKey(release.ReleaseNameWithRuntime, release.PlatformString);
            var existing = existingDocument?.Installations.GetValueOrDefault(key);
            installations.Add(new Installation(
                key,
                release.ReleaseNameWithRuntime,
                release.PlatformString,
                NormalizeRelativePath(info.Name),
                existing?.InstalledAt ?? GetDirectoryCreatedAt(info.FullName),
                existing?.LastLaunchedAt));
        }

        return new Result<IReadOnlyList<Installation>, FileOperationError>.Success(installations);
    }

    private Result<IReadOnlyList<Installation>, FileOperationError> ScanTargetAwareInstallations(InstallationRegistryDocument? existingDocument)
    {
        switch (hostSystem.DirectoryExists(pathService.InstallationsDirectoryPath))
        {
            case Result<bool, FileOperationError>.Failure(var existsError):
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Failure(existsError);
            case Result<bool, FileOperationError>.Success { Value: false }:
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Success([]);
            case Result<bool, FileOperationError>.Success:
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        IReadOnlyList<HostDirectoryEntry> releases;
        switch (hostSystem.EnumerateDirectories(pathService.InstallationsDirectoryPath))
        {
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var releaseDirectoriesError):
                return new Result<IReadOnlyList<Installation>, FileOperationError>.Failure(releaseDirectoriesError);
            case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                releases = entries;
                break;
            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        var installations = new List<Installation>();
        foreach (var releaseInfo in releases)
        {
            if (Release.TryParse(releaseInfo.Name) is not { } release)
            {
                continue;
            }

            IReadOnlyList<HostDirectoryEntry> targets;
            switch (hostSystem.EnumerateDirectories(releaseInfo.FullName))
            {
                case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Failure(var targetDirectoriesError):
                    return new Result<IReadOnlyList<Installation>, FileOperationError>.Failure(targetDirectoriesError);
                case Result<IReadOnlyList<HostDirectoryEntry>, FileOperationError>.Success(var entries):
                    targets = entries;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            foreach (var targetInfo in targets)
            {
                if (targetInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
                    targetInfo.Name.StartsWith(".", StringComparison.Ordinal) ||
                    !IsSafeTarget(targetInfo.Name))
                {
                    continue;
                }

                var key = CreateKey(release.ReleaseNameWithRuntime, targetInfo.Name);
                var relativePath = ToRelativePath(targetInfo.FullName);
                var existing = existingDocument?.Installations.GetValueOrDefault(key);
                installations.Add(new Installation(
                    key,
                    release.ReleaseNameWithRuntime,
                    targetInfo.Name,
                    relativePath,
                    existing?.InstalledAt ?? GetDirectoryCreatedAt(targetInfo.FullName),
                    existing?.LastLaunchedAt));
            }
        }

        return new Result<IReadOnlyList<Installation>, FileOperationError>.Success(installations);
    }

    private string? InferDefaultFromSymlink(InstallationRegistryDocument document)
    {
        if (hostSystem.ResolveCurrentSymlinks() is Result<SymlinkInfo, SymlinkError>.Success(var symlinkInfo))
        {
            return FindInstallationContainingTarget(document, symlinkInfo.SymlinkPath);
        }

        return null;
    }

    private string? FindInstallationContainingTarget(InstallationRegistryDocument document, string targetPath)
    {
        var symlinkTarget = ResolveSymlinkTarget(targetPath);
        if (symlinkTarget is null)
        {
            return null;
        }

        return document.Installations
            .Select(kvp => new { kvp.Key, FullPath = ResolvePath(kvp.Value.Path) })
            .Where(x => IsSubPathOrSame(symlinkTarget, x.FullPath))
            .OrderByDescending(x => x.FullPath.Length)
            .Select(x => x.Key)
            .FirstOrDefault();
    }

    private void RefreshDefaultArtifacts(InstallationRegistryDocument document)
    {
        if (document.Default is null ||
            !document.Installations.TryGetValue(document.Default, out var entry))
        {
            return;
        }

        if (TryParseKey(document.Default, out var releaseNameWithRuntime, out var target) &&
            releaseManager.CreateRelease(releaseNameWithRuntime) is Result<Release, ReleaseParseError>.Success(var release))
        {
            release = release with { PlatformString = target };
            var fgvmExecutablePath = Path.GetFullPath(System.Environment.ProcessPath ?? System.Environment.GetCommandLineArgs().First());
            if (hostSystem.EnsureShim(fgvmExecutablePath) is Result<Unit, ShimError>.Failure(var shimError))
            {
                logger.LogWarning("Failed to update shim after generating installation registry: {Error}", shimError);
            }

            var symlinkTargetPath = Path.Combine(ResolvePath(entry.Path), release.ExecName);
            if (hostSystem.CreateOrOverwriteSymbolicLink(symlinkTargetPath) is Result<Unit, SymlinkError>.Failure(var symlinkError))
            {
                logger.LogWarning("Failed to refresh Godot symlink after generating installation registry: {Error}", symlinkError);
            }
        }
    }

    private string? ResolveSymlinkTarget(string path)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path);
            return (hostSystem.FileExists(normalizedPath), hostSystem.DirectoryExists(normalizedPath)) switch
            {
                (Result<bool, FileOperationError>.Failure, _) or (_, Result<bool, FileOperationError>.Failure) => null,
                (Result<bool, FileOperationError>.Success { Value: false }, Result<bool, FileOperationError>.Success { Value: false }) => normalizedPath,
                (Result<bool, FileOperationError>.Success, Result<bool, FileOperationError>.Success) =>
                    hostSystem.ResolveLinkTarget(normalizedPath, true) is Result<string?, FileOperationError>.Success(var target)
                        ? target ?? normalizedPath
                        : null,
                _ => throw new InvalidOperationException("Unexpected Result type")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private Result<Unit, InstallationRegistryError> WriteDocument(InstallationRegistryDocument document)
    {
        var tempPath = $"{pathService.InstallationsPath}.{Guid.NewGuid():N}.tmp";
        if (hostSystem.CreateDirectory(pathService.RootPath) is Result<Unit, FileOperationError>.Failure(var createDirectoryError))
        {
            return new Result<Unit, InstallationRegistryError>.Failure(
                new InstallationRegistryError.WriteFailed(createDirectoryError));
        }

        var writeResult = hostSystem.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(document, InstallationRegistryJsonContext.Default.InstallationRegistryDocument));
        if (writeResult is Result<Unit, FileOperationError>.Failure(var writeError))
        {
            return new Result<Unit, InstallationRegistryError>.Failure(
                new InstallationRegistryError.WriteFailed(writeError));
        }

        var moveResult = hostSystem.MoveFile(tempPath, pathService.InstallationsPath, true);
        if (moveResult is Result<Unit, FileOperationError>.Failure(var moveError))
        {
            if (hostSystem.DeleteFileIfExists(tempPath) is Result<Unit, FileOperationError>.Failure(var cleanupError))
            {
                logger.LogWarning("Failed to delete temporary installation registry file {TempPath}: {Error}", tempPath, cleanupError);
            }

            return new Result<Unit, InstallationRegistryError>.Failure(
                new InstallationRegistryError.WriteFailed(moveError));
        }

        return new Result<Unit, InstallationRegistryError>.Success(Unit.Value);
    }

    public static string CreateKey(string releaseNameWithRuntime, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("Release platform string is required for installation registry keys.");
        }

        return $"{releaseNameWithRuntime}@{target}";
    }

    public static string CreateRelativeInstallPath(Release release)
    {
        if (string.IsNullOrWhiteSpace(release.PlatformString))
        {
            throw new InvalidOperationException("Release platform string is required for installation registry paths.");
        }

        return string.Join('/', InstallationsDirectoryName, release.ReleaseNameWithRuntime, release.PlatformString);
    }

    public string ResolveInstallationPath(Installation installation) => ResolvePath(installation.RelativePath);

    private string ResolvePath(string relativePath) => Path.GetFullPath(Path.Combine(pathService.RootPath, relativePath));

    private string ToRelativePath(string fullPath) =>
        NormalizeRelativePath(Path.GetRelativePath(pathService.RootPath, Path.GetFullPath(fullPath)));

    private static Installation ToInstalledGodot(string key, InstallationRegistryEntry entry)
    {
        if (!TryParseKey(key, out var releaseName, out var target))
        {
            throw new InvalidOperationException($"Invalid installation key `{key}`.");
        }

        return new Installation(key, releaseName, target, entry.Path, entry.InstalledAt, entry.LastLaunchedAt);
    }

    private static InstallationRegistryEntry ToEntry(Installation installation) =>
        new()
        {
            Path = installation.RelativePath,
            InstalledAt = installation.InstalledAt,
            LastLaunchedAt = installation.LastLaunchedAt
        };

    private static bool TryParseKey(string key, out string releaseNameWithRuntime, out string target)
    {
        var separatorIndex = key.LastIndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == key.Length - 1)
        {
            releaseNameWithRuntime = "";
            target = "";
            return false;
        }

        releaseNameWithRuntime = key[..separatorIndex];
        target = key[(separatorIndex + 1)..];
        return Release.TryParse(releaseNameWithRuntime) is not null && IsSafeTarget(target);
    }

    private bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            Path.IsPathFullyQualified(relativePath))
        {
            return false;
        }

        var fullPath = ResolvePath(relativePath);
        return IsSubPathOrSame(fullPath, pathService.RootPath);
    }

    private static bool IsSafeTarget(string target) =>
        !string.IsNullOrWhiteSpace(target) &&
        target.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !target.Contains(Path.DirectorySeparatorChar) &&
        !target.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsSubPathOrSame(string path, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var normalizedPath = Path.GetFullPath(path);

        return string.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized + Path.DirectorySeparatorChar;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private DateTimeOffset? GetDirectoryCreatedAt(string path)
    {
        return hostSystem.GetDirectoryCreatedAtUtc(path) is Result<DateTimeOffset, FileOperationError>.Success(var createdAt)
            ? createdAt
            : null;
    }

    private sealed record RegistryValidation(
        InstallationRegistryDocument Document,
        bool Changed,
        bool NeedsFileSystemGeneration);
}

public sealed class InstallationRegistryDocument
{
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("installations")]
    public Dictionary<string, InstallationRegistryEntry> Installations { get; set; } = [];
}

public sealed class InstallationRegistryEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("installedAt")]
    public DateTimeOffset? InstalledAt { get; set; }

    [JsonPropertyName("lastLaunchedAt")]
    public DateTimeOffset? LastLaunchedAt { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InstallationRegistryDocument))]
internal partial class InstallationRegistryJsonContext : JsonSerializerContext;
