using System.Text.Json;
using System.Text.Json.Serialization;
using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;

namespace Fgvm.Godot;

public enum ReleaseFetchMode
{
    UseCache,
    ForceRemote
}

internal abstract record ReleaseCatalogRead
{
    public sealed record Found(ReleaseCatalogManifest Manifest) : ReleaseCatalogRead;

    public sealed record MissingOrInvalid : ReleaseCatalogRead;
}

/// <summary>
///     Reads and updates the local releases catalog used to resolve searchable releases and artifact metadata.
/// </summary>
public interface IReleaseCatalog
{
    /// <summary>
    ///     Reads available release identifiers from the local catalog or remote source.
    /// </summary>
    /// <param name="fetchMode">Whether to use the local cache or force a remote refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Release identifiers, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when release lookup is canceled.</exception>
    Task<Result<string[], NetworkError>> ReadReleaseIds(ReleaseFetchMode fetchMode, CancellationToken cancellationToken);

    /// <summary>
    ///     Finds artifact metadata for a release, hydrating the local catalog from remote metadata when needed.
    /// </summary>
    /// <param name="release">The release whose artifact should be found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The release artifact, or a network error.</returns>
    /// <exception cref="OperationCanceledException">Thrown when artifact lookup is canceled.</exception>
    Task<Result<ReleaseArtifact, NetworkError>> FindOrHydrateArtifact(Release release, CancellationToken cancellationToken);
}

public sealed class ReleaseCatalog(
    IDownloadClient downloadClient,
    IPathService pathService,
    IHostSystem hostSystem,
    ILogger<ReleaseCatalog> logger
) : IReleaseCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public async Task<Result<string[], NetworkError>> ReadReleaseIds(ReleaseFetchMode fetchMode, CancellationToken cancellationToken)
    {
        var cachedResult = await ReadCatalogState(cancellationToken);

        switch (cachedResult)
        {
            case Result<ReleaseCatalogRead, NetworkError>.Failure(var error):
                return new Result<string[], NetworkError>.Failure(error);

            case Result<ReleaseCatalogRead, NetworkError>.Success { Value: ReleaseCatalogRead.Found(var manifest) }
                when fetchMode == ReleaseFetchMode.UseCache && IsReleaseIndexFresh(manifest) && GetReleaseIds(manifest).Any():
                return new Result<string[], NetworkError>.Success(SortReleaseIds(GetReleaseIds(manifest)));

            case Result<ReleaseCatalogRead, NetworkError>.Success(var read):
                var refreshResult = await RefreshCatalog(GetExistingManifest(read), cancellationToken);
                return refreshResult switch
                {
                    Result<ReleaseCatalogManifest, NetworkError>.Success(var refreshed) =>
                        new Result<string[], NetworkError>.Success(SortReleaseIds(GetReleaseIds(refreshed))),
                    Result<ReleaseCatalogManifest, NetworkError>.Failure(var error) =>
                        new Result<string[], NetworkError>.Failure(error),
                    _ => throw new InvalidOperationException("Unexpected Result type")
                };

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ReleaseArtifact, NetworkError>> FindOrHydrateArtifact(Release release, CancellationToken cancellationToken)
    {
        var manifestResult = await ReadCatalogOrRebuild(cancellationToken);
        switch (manifestResult)
        {
            case Result<ReleaseCatalogManifest, NetworkError>.Failure(var catalogError):
                return new Result<ReleaseArtifact, NetworkError>.Failure(catalogError);

            case Result<ReleaseCatalogManifest, NetworkError>.Success(var manifest)
                when FindArtifact(manifest, release) is { } cached:
                return new Result<ReleaseArtifact, NetworkError>.Success(cached);

            case Result<ReleaseCatalogManifest, NetworkError>.Success(var manifest):
                var remoteManifestResult = await downloadClient.GetReleaseManifest(release, cancellationToken);
                switch (remoteManifestResult)
                {
                    case Result<GodotReleaseManifest, NetworkError>.Success(var godotManifest) when godotManifest.Files.Count > 0:
                        var recordResult = await RecordReleaseManifest(manifest, godotManifest, release, cancellationToken);
                        return recordResult switch
                        {
                            Result<ReleaseCatalogManifest, NetworkError>.Success(var recorded) =>
                                FindArtifact(recorded, release) is { } artifact
                                    ? new Result<ReleaseArtifact, NetworkError>.Success(artifact)
                                    : new Result<ReleaseArtifact, NetworkError>.Failure(new NetworkError.ConnectionFailure(
                                        $"No artifact found for {release.ReleaseNameWithRuntime} on target {GetCatalogTargetId(release)}")),
                            Result<ReleaseCatalogManifest, NetworkError>.Failure(var error) =>
                                new Result<ReleaseArtifact, NetworkError>.Failure(error),
                            _ => throw new InvalidOperationException("Unexpected Result type")
                        };

                    case Result<GodotReleaseManifest, NetworkError>.Success:
                        return await HydrateFromSha512Sums(manifest, release, cancellationToken);

                    case Result<GodotReleaseManifest, NetworkError>.Failure(var error):
                        return new Result<ReleaseArtifact, NetworkError>.Failure(error);

                    default:
                        throw new InvalidOperationException("Unexpected Result type");
                }

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private async Task<Result<ReleaseArtifact, NetworkError>> HydrateFromSha512Sums(ReleaseCatalogManifest manifest,
        Release release,
        CancellationToken cancellationToken
    )
    {
        var shaResult = await downloadClient.GetSha512(release, cancellationToken);
        switch (shaResult)
        {
            case Result<string, NetworkError>.Success(var sha512Sums):
                var recordResult = await RecordArtifacts(manifest, release, ParseSha512SumsContent(sha512Sums), cancellationToken);
                return recordResult switch
                {
                    Result<ReleaseCatalogManifest, NetworkError>.Success(var recorded) =>
                        new Result<ReleaseArtifact, NetworkError>.Success(
                            FindArtifact(recorded, release) ?? new ReleaseArtifact(release.ZipFileName, null)),
                    Result<ReleaseCatalogManifest, NetworkError>.Failure(var error) =>
                        new Result<ReleaseArtifact, NetworkError>.Failure(error),
                    _ => throw new InvalidOperationException("Unexpected Result type")
                };

            case Result<string, NetworkError>.Failure(var error):
                logger.LogWarning(
                    "Failed to hydrate {ReleaseName} from SHA512-SUMS.txt: {Error}. Continuing without catalog artifact metadata",
                    release.ReleaseName,
                    error);

                return new Result<ReleaseArtifact, NetworkError>.Success(new ReleaseArtifact(release.ZipFileName, null));

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private async Task<Result<ReleaseCatalogManifest, NetworkError>> RecordReleaseManifest(ReleaseCatalogManifest manifest,
        GodotReleaseManifest godotManifest,
        Release fallbackRelease,
        CancellationToken cancellationToken
    )
    {
        var release = Release.TryParse(godotManifest.Name) ?? fallbackRelease;
        var artifacts = godotManifest.Files
            .Select(file => new ReleaseCatalogArtifactEntry(file.FileName, file.Checksum))
            .ToArray();

        var shaResult = await downloadClient.GetSha512(fallbackRelease, cancellationToken);
        switch (shaResult)
        {
            case Result<string, NetworkError>.Success(var sha512Sums):
                artifacts = OverlaySha512Sums(artifacts, ParseSha512SumsContent(sha512Sums));
                break;

            case Result<string, NetworkError>.Failure(NetworkError.RequestFailure { StatusCode: 404 }):
                logger.LogInformation(
                    "No SHA512-SUMS.txt found for {ReleaseName}. Using release manifest checksums",
                    fallbackRelease.ReleaseName);
                break;

            case Result<string, NetworkError>.Failure(var error):
                logger.LogWarning(
                    "Failed to overlay {ReleaseName} checksums from SHA512-SUMS.txt: {Error}. Using release manifest checksums",
                    fallbackRelease.ReleaseName,
                    error);
                break;

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        return await RecordArtifacts(
            manifest,
            release,
            artifacts,
            cancellationToken,
            godotManifest.ReleaseDate,
            godotManifest.GitReference);
    }

    private static ReleaseCatalogArtifactEntry[] OverlaySha512Sums(IReadOnlyList<ReleaseCatalogArtifactEntry> artifacts,
        IEnumerable<ReleaseCatalogArtifactEntry> sha512Sums
    )
    {
        var checksums = sha512Sums
            .GroupBy(entry => entry.FileName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Sha512, StringComparer.Ordinal);

        return artifacts
            .Select(entry => checksums.TryGetValue(entry.FileName, out var sha512)
                ? entry with { Sha512 = sha512 }
                : entry)
            .ToArray();
    }

    private async Task<Result<ReleaseCatalogManifest, NetworkError>> RecordArtifacts(ReleaseCatalogManifest manifest,
        Release release,
        IEnumerable<ReleaseCatalogArtifactEntry> artifacts,
        CancellationToken cancellationToken,
        long? releaseDate = null,
        string? gitReference = null
    )
    {
        var catalogRelease = GetOrCreateRelease(manifest, release);
        catalogRelease.ReleaseDate = releaseDate ?? catalogRelease.ReleaseDate;
        catalogRelease.GitReference = gitReference ?? catalogRelease.GitReference;

        foreach (var entry in artifacts)
        {
            var artifact = new ReleaseCatalogArtifact
            {
                FileName = entry.FileName,
                Sha512 = entry.Sha512
            };

            catalogRelease.Files[entry.FileName] = artifact;

            if (!TryGetTargetAndRuntime(release, entry.FileName, out var targetId, out var runtimeId))
            {
                continue;
            }

            if (!catalogRelease.Targets.TryGetValue(targetId, out var target))
            {
                target = [];
                catalogRelease.Targets[targetId] = target;
            }

            target[runtimeId] = artifact;
        }

        var writeResult = await WriteCache(manifest, cancellationToken);
        return writeResult switch
        {
            Result<Unit, NetworkError>.Success =>
                new Result<ReleaseCatalogManifest, NetworkError>.Success(manifest),
            Result<Unit, NetworkError>.Failure(var error) =>
                new Result<ReleaseCatalogManifest, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private static bool IsReleaseIndexFresh(ReleaseCatalogManifest manifest) =>
        manifest.LastUpdated is { } lastUpdated &&
        DateTimeOffset.UtcNow - lastUpdated <= CacheTtl;

    private Task<Result<ReleaseCatalogRead, NetworkError>> ReadCatalogState(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (hostSystem.FileExists(pathService.ReleasesPath))
        {
            case Result<bool, FileOperationError>.Failure(var existsError):
                return Task.FromResult<Result<ReleaseCatalogRead, NetworkError>>(
                    new Result<ReleaseCatalogRead, NetworkError>.Failure(
                        new NetworkError.CacheReadFailure(existsError)));

            case Result<bool, FileOperationError>.Success { Value: false }:
                return Task.FromResult<Result<ReleaseCatalogRead, NetworkError>>(
                    new Result<ReleaseCatalogRead, NetworkError>.Success(new ReleaseCatalogRead.MissingOrInvalid()));

            case Result<bool, FileOperationError>.Success:
                break;

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }

        switch (hostSystem.ReadAllText(pathService.ReleasesPath))
        {
            case Result<string, FileOperationError>.Failure(var readError):
                return Task.FromResult<Result<ReleaseCatalogRead, NetworkError>>(
                    new Result<ReleaseCatalogRead, NetworkError>.Failure(
                        new NetworkError.CacheReadFailure(readError)));

            case Result<string, FileOperationError>.Success(var json):
                try
                {
                    var manifest = JsonSerializer.Deserialize(json, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest);
                    var result = manifest is { LastUpdated: not null, Releases: not null }
                        ? new Result<ReleaseCatalogRead, NetworkError>.Success(new ReleaseCatalogRead.Found(manifest))
                        : new Result<ReleaseCatalogRead, NetworkError>.Success(new ReleaseCatalogRead.MissingOrInvalid());

                    return Task.FromResult<Result<ReleaseCatalogRead, NetworkError>>(result);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Release catalog at {ReleasesPath} is invalid; rebuilding from remote", pathService.ReleasesPath);
                    return Task.FromResult<Result<ReleaseCatalogRead, NetworkError>>(
                        new Result<ReleaseCatalogRead, NetworkError>.Success(new ReleaseCatalogRead.MissingOrInvalid()));
                }

            default:
                throw new InvalidOperationException("Unexpected Result type");
        }
    }

    private async Task<Result<Unit, NetworkError>> WriteCache(ReleaseCatalogManifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tempPath = $"{pathService.ReleasesPath}.{Guid.NewGuid():N}.tmp";

        if (Path.GetDirectoryName(pathService.ReleasesPath) is { } directory &&
            hostSystem.CreateDirectory(directory) is Result<Unit, FileOperationError>.Failure(var createDirectoryError))
        {
            return new Result<Unit, NetworkError>.Failure(new NetworkError.CacheWriteFailure(createDirectoryError));
        }

        var writeResult = hostSystem.WriteAllText(
            tempPath,
            JsonSerializer.Serialize(manifest, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest));
        if (writeResult is Result<Unit, FileOperationError>.Failure(var writeError))
        {
            return new Result<Unit, NetworkError>.Failure(new NetworkError.CacheWriteFailure(writeError));
        }

        var moveResult = hostSystem.MoveFile(tempPath, pathService.ReleasesPath, true);
        if (moveResult is Result<Unit, FileOperationError>.Failure(var moveError))
        {
            if (hostSystem.DeleteFileIfExists(tempPath) is Result<Unit, FileOperationError>.Failure(var cleanupError))
            {
                logger.LogWarning("Failed to delete temporary release cache file {TempPath}: {Error}", tempPath, cleanupError);
            }

            return new Result<Unit, NetworkError>.Failure(new NetworkError.CacheWriteFailure(moveError));
        }

        await Task.CompletedTask;
        return new Result<Unit, NetworkError>.Success(Unit.Value);
    }
    private async Task<Result<ReleaseCatalogManifest, NetworkError>> ReadCatalogOrRebuild(CancellationToken cancellationToken)
    {
        var readResult = await ReadCatalogState(cancellationToken);
        return readResult switch
        {
            Result<ReleaseCatalogRead, NetworkError>.Success { Value: ReleaseCatalogRead.Found(var manifest) } =>
                new Result<ReleaseCatalogManifest, NetworkError>.Success(manifest),
            Result<ReleaseCatalogRead, NetworkError>.Success =>
                await RefreshCatalog(null, cancellationToken),
            Result<ReleaseCatalogRead, NetworkError>.Failure(var error) =>
                new Result<ReleaseCatalogManifest, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private async Task<Result<ReleaseCatalogManifest, NetworkError>> RefreshCatalog(ReleaseCatalogManifest? existing,
        CancellationToken cancellationToken
    )
    {
        var remote = await downloadClient.ListReleases(cancellationToken);
        return remote switch
        {
            Result<IEnumerable<string>, NetworkError>.Success(var releaseIds) =>
                await WriteReleaseIndex(releaseIds, existing, cancellationToken),
            Result<IEnumerable<string>, NetworkError>.Failure(var error) =>
                new Result<ReleaseCatalogManifest, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private async Task<Result<ReleaseCatalogManifest, NetworkError>> WriteReleaseIndex(IEnumerable<string> remoteReleaseIds,
        ReleaseCatalogManifest? existing,
        CancellationToken cancellationToken
    )
    {
        var manifest = CreateManifest(remoteReleaseIds, existing);
        manifest.LastUpdated = DateTimeOffset.UtcNow;

        var writeResult = await WriteCache(manifest, cancellationToken);
        return writeResult switch
        {
            Result<Unit, NetworkError>.Success =>
                new Result<ReleaseCatalogManifest, NetworkError>.Success(manifest),
            Result<Unit, NetworkError>.Failure(var error) =>
                new Result<ReleaseCatalogManifest, NetworkError>.Failure(error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    private static ReleaseCatalogManifest CreateManifest(IEnumerable<string> releaseIds, ReleaseCatalogManifest? existing)
    {
        var manifest = new ReleaseCatalogManifest();

        foreach (var release in SortReleaseIds(releaseIds)
                     .Select(Release.TryParse)
                     .OfType<Release>())
        {
            var releaseTypeId = GetReleaseTypeId(release);

            if (!manifest.Releases.TryGetValue(release.Version, out var version))
            {
                version = [];
                manifest.Releases[release.Version] = version;
            }

            version[releaseTypeId] = existing?.Releases is not null &&
                                     existing.Releases.TryGetValue(release.Version, out var existingVersion) &&
                                     existingVersion.TryGetValue(releaseTypeId, out var catalogRelease)
                ? catalogRelease
                : new ReleaseCatalogRelease();
        }

        return manifest;
    }

    private static ReleaseCatalogRelease GetOrCreateRelease(ReleaseCatalogManifest manifest, Release release)
    {
        var releaseTypeId = GetReleaseTypeId(release);

        if (!manifest.Releases.TryGetValue(release.Version, out var version))
        {
            version = [];
            manifest.Releases[release.Version] = version;
        }

        if (!version.TryGetValue(releaseTypeId, out var catalogRelease))
        {
            catalogRelease = new ReleaseCatalogRelease();
            version[releaseTypeId] = catalogRelease;
        }

        return catalogRelease;
    }

    private static IEnumerable<string> GetReleaseIds(ReleaseCatalogManifest manifest) =>
        manifest.Releases.SelectMany(version => version.Value.Keys.Select(releaseTypeId => $"{version.Key}-{releaseTypeId}"));

    private static ReleaseArtifact? FindArtifact(ReleaseCatalogManifest manifest, Release release)
    {
        var targetId = GetCatalogTargetId(release);
        var runtimeId = release.RuntimeEnvironment.Name();

        var releaseTypeId = GetReleaseTypeId(release);
        return manifest.Releases.TryGetValue(release.Version, out var version) &&
               version.TryGetValue(releaseTypeId, out var catalogRelease) &&
               catalogRelease.Targets.TryGetValue(targetId, out var target) &&
               target.TryGetValue(runtimeId, out var artifact)
            ? new ReleaseArtifact(artifact.FileName, artifact.Sha512)
            : null;
    }

    private static ReleaseCatalogManifest? GetExistingManifest(ReleaseCatalogRead read) =>
        read is ReleaseCatalogRead.Found(var manifest) ? manifest : null;

    private static string[] SortReleaseIds(IEnumerable<string> releaseIds) =>
        releaseIds
            .Select(Release.TryParse)
            .OfType<Release>()
            .OrderByDescending(release => release)
            .Select(release => release.ReleaseName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string GetReleaseTypeId(Release release) =>
        release.Type?.ToString() ?? "unknown";

    internal static IReadOnlyList<ReleaseCatalogArtifactEntry> ParseSha512SumsContent(string content)
    {
        var artifacts = new List<ReleaseCatalogArtifactEntry>();
        var lines = content.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Split([' '], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            var sha512 = parts[0].Trim();
            if (sha512.Length != 128 || sha512.Any(character => !Uri.IsHexDigit(character)))
            {
                continue;
            }

            artifacts.Add(new ReleaseCatalogArtifactEntry(parts[1].Trim(), sha512));
        }

        return artifacts;
    }

    private static bool TryGetTargetAndRuntime(Release release, string fileName, out string targetId, out string runtimeId)
    {
        targetId = "";
        runtimeId = "";

        const string archiveSuffix = ".zip";
        var prefix = $"Godot_v{release.ReleaseName}_";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(archiveSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawTarget = fileName[prefix.Length..^archiveSuffix.Length];
        if (rawTarget.Contains("export_templates", StringComparison.OrdinalIgnoreCase) ||
            rawTarget.Equals("web_editor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        runtimeId = rawTarget.StartsWith("mono_", StringComparison.OrdinalIgnoreCase)
            ? RuntimeEnvironment.Mono.Name()
            : RuntimeEnvironment.Standard.Name();

        targetId = PlatformStringProvider.GetCatalogTargetId(rawTarget);
        return true;
    }

    private static string GetCatalogTargetId(Release release) =>
        PlatformStringProvider.GetCatalogTargetId(release.PlatformString);
}

public sealed record ReleaseCatalogArtifactEntry(string FileName, string Sha512);

/// <summary>
///     Version -> release type -> target/platform -> runtime -> artifact.
/// </summary>
public sealed class ReleaseCatalogManifest
{
    [JsonPropertyName("lastUpdated")]
    public DateTimeOffset? LastUpdated { get; set; }

    [JsonPropertyName("releases")]
    public Dictionary<string, ReleaseCatalogVersion> Releases { get; init; } = [];
}

public sealed class ReleaseCatalogVersion : Dictionary<string, ReleaseCatalogRelease>;

public sealed class ReleaseCatalogRelease
{
    [JsonPropertyName("releaseDate")]
    public long? ReleaseDate { get; set; }

    [JsonPropertyName("gitReference")]
    public string? GitReference { get; set; }

    [JsonPropertyName("targets")]
    public ReleaseCatalogTargets Targets { get; init; } = [];

    [JsonPropertyName("files")]
    public Dictionary<string, ReleaseCatalogArtifact> Files { get; init; } = [];
}

public sealed class ReleaseCatalogTargets : Dictionary<string, ReleaseCatalogTarget>;

public sealed class ReleaseCatalogTarget : Dictionary<string, ReleaseCatalogArtifact>;

public sealed class ReleaseCatalogArtifact
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("sha512")]
    public required string Sha512 { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReleaseCatalogManifest))]
internal partial class ReleaseCatalogJsonContext : JsonSerializerContext;
