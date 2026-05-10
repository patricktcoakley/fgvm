using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fgvm.Godot;

public enum ReleaseFetchMode
{
    UseCache,
    ForceRemote
}

/// <summary>
///     Reads and updates the local releases catalog used to resolve searchable releases and artifact metadata.
/// </summary>
public interface IReleaseCatalog
{
    Task<Result<string[], NetworkError>> ReadReleaseIds(ReleaseFetchMode fetchMode, CancellationToken cancellationToken);
    Task<ReleaseCatalogArtifact?> FindArtifact(Release release, CancellationToken cancellationToken);
    Task RecordArtifact(Release release, string fileName, string sha512, CancellationToken cancellationToken);
}

public sealed class ReleaseCatalog(
    IDownloadClient downloadClient,
    IPathService pathService,
    ILogger<ReleaseCatalog> logger) : IReleaseCatalog
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(1);

    public async Task<Result<string[], NetworkError>> ReadReleaseIds(ReleaseFetchMode fetchMode, CancellationToken cancellationToken)
    {
        if (fetchMode == ReleaseFetchMode.UseCache && IsCacheFresh())
        {
            var cached = await ReadCachedReleaseIds(cancellationToken);
            if (cached.Length > 0)
            {
                return new Result<string[], NetworkError>.Success(cached);
            }
        }

        var remote = await downloadClient.ListReleases(cancellationToken);
        return remote switch
        {
            Result<IEnumerable<string>, NetworkError>.Success success => await RefreshCache(success.Value, cancellationToken),
            Result<IEnumerable<string>, NetworkError>.Failure failure => new Result<string[], NetworkError>.Failure(failure.Error),
            _ => throw new InvalidOperationException("Unexpected Result type")
        };
    }

    public async Task<ReleaseCatalogArtifact?> FindArtifact(Release release, CancellationToken cancellationToken)
    {
        var manifest = await TryReadManifest(cancellationToken);
        if (manifest is null)
        {
            return null;
        }

        var targetId = GetCatalogTargetId(release);
        var runtimeId = release.RuntimeEnvironment.Name();

        var releaseTypeId = GetReleaseTypeId(release);
        return manifest.TryGetValue(release.Version, out var version) &&
               version.TryGetValue(releaseTypeId, out var catalogRelease) &&
               catalogRelease.TryGetValue(targetId, out var target) &&
               target.TryGetValue(runtimeId, out var artifact)
            ? artifact
            : null;
    }

    public async Task RecordArtifact(Release release, string fileName, string sha512, CancellationToken cancellationToken)
    {
        var manifest = await TryReadManifest(cancellationToken) ?? [];
        var targetId = GetCatalogTargetId(release);
        var runtimeId = release.RuntimeEnvironment.Name();

        var releaseTypeId = GetReleaseTypeId(release);

        if (!manifest.TryGetValue(release.Version, out var version))
        {
            version = [];
            manifest[release.Version] = version;
        }

        if (!version.TryGetValue(releaseTypeId, out var catalogRelease))
        {
            catalogRelease = [];
            version[releaseTypeId] = catalogRelease;
        }

        if (!catalogRelease.TryGetValue(targetId, out var target))
        {
            target = [];
            catalogRelease[targetId] = target;
        }

        target[runtimeId] = new ReleaseCatalogArtifact
        {
            FileName = fileName,
            Sha512 = sha512
        };

        await WriteCacheBestEffort(manifest, cancellationToken);
    }

    private bool IsCacheFresh()
    {
        try
        {
            var file = new FileInfo(pathService.ReleasesPath);
            return file.Exists && DateTimeOffset.UtcNow - file.LastWriteTimeUtc <= CacheTtl;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Failed to inspect release catalog at {ReleasesPath}; refreshing from remote", pathService.ReleasesPath);
            return false;
        }
    }

    private async Task<string[]> ReadCachedReleaseIds(CancellationToken cancellationToken)
    {
        var manifest = await TryReadManifest(cancellationToken);
        if (manifest is null)
        {
            return [];
        }

        return SortReleaseIds(GetReleaseIds(manifest));
    }

    private async Task<ReleaseCatalogManifest?> TryReadManifest(CancellationToken cancellationToken)
    {
        if (!File.Exists(pathService.ReleasesPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(pathService.ReleasesPath);
            return await JsonSerializer.DeserializeAsync(stream, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to read release catalog at {ReleasesPath}; refreshing from remote", pathService.ReleasesPath);
            return null;
        }
    }

    private async Task WriteCacheBestEffort(ReleaseCatalogManifest manifest, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pathService.ReleasesPath)!);
            await using var stream = File.Create(pathService.ReleasesPath);
            await JsonSerializer.SerializeAsync(stream, manifest, ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to write release catalog at {ReleasesPath}; continuing with remote release ids", pathService.ReleasesPath);
        }
    }

    private async Task<Result<string[], NetworkError>> RefreshCache(IEnumerable<string> remoteReleaseIds, CancellationToken cancellationToken)
    {
        var existing = await TryReadManifest(cancellationToken);
        var manifest = CreateManifest(remoteReleaseIds, existing);
        var releaseIds = SortReleaseIds(GetReleaseIds(manifest));

        await WriteCacheBestEffort(manifest, cancellationToken);

        return new Result<string[], NetworkError>.Success(releaseIds);
    }

    private static ReleaseCatalogManifest CreateManifest(IEnumerable<string> releaseIds, ReleaseCatalogManifest? existing)
    {
        var manifest = new ReleaseCatalogManifest();

        foreach (var release in SortReleaseIds(releaseIds)
                     .Select(Release.TryParse)
                     .OfType<Release>())
        {
            var releaseTypeId = GetReleaseTypeId(release);

            if (!manifest.TryGetValue(release.Version, out var version))
            {
                version = [];
                manifest[release.Version] = version;
            }

            version[releaseTypeId] = existing is not null &&
                                     existing.TryGetValue(release.Version, out var existingVersion) &&
                                     existingVersion.TryGetValue(releaseTypeId, out var catalogRelease)
                ? catalogRelease
                : [];
        }

        return manifest;
    }

    private static IEnumerable<string> GetReleaseIds(ReleaseCatalogManifest manifest) =>
        manifest.SelectMany(version => version.Value.Keys.Select(releaseTypeId => $"{version.Key}-{releaseTypeId}"));

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

    private static string GetCatalogTargetId(Release release) =>
        release.PlatformString switch
        {
            null => "unknown",
            "mono_macos.universal" => "macos.universal",
            "mono_osx.universal" => "osx.universal",
            "mono_osx.64" or "mono_osx64" => "osx.64",
            "mono_linux_x86_64" => "linux.x86_64",
            "mono_linux_x86_32" => "linux.x86_32",
            "mono_linux_arm32" => "linux.arm32",
            "mono_linux_arm64" => "linux.arm64",
            "mono_x11_64" => "x11.64",
            "mono_x11_32" => "x11.32",
            "mono_windows_arm64" => "windows_arm64",
            "mono_win64" => "win64",
            "mono_win32" => "win32",
            var target when target.EndsWith(".exe", StringComparison.Ordinal) => target[..^4],
            var target => target
        };
}

/// <summary>
///     Version -> release type -> target/platform -> runtime -> artifact.
/// </summary>
public sealed class ReleaseCatalogManifest : Dictionary<string, ReleaseCatalogVersion>
{
}

public sealed class ReleaseCatalogVersion : Dictionary<string, ReleaseCatalogRelease>
{
}

public sealed class ReleaseCatalogRelease : Dictionary<string, ReleaseCatalogTarget>
{
}

public sealed class ReleaseCatalogTarget : Dictionary<string, ReleaseCatalogArtifact>
{
}

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
